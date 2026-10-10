using Inventory.Domain.Entities;
using Inventory.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Inventory.ConcurrencyTests;

// اختبارات تكامل تُثبت أن التزامن المتفائل (xmin) على StockItem يعمل فعلياً مقابل Postgres حيّ.
// نقود السباق بشكل حتمي (ليس حظ توقيت): نقرأ نفس الصف في سياقين منفصلين فيلتقطان نفس قيمة xmin،
// ثم يحاول كلاهما الحفظ. الحفظ الثاني يفشل حتماً بـ DbUpdateConcurrencyException لأن xmin تغيّر.
// ثم نُثبت أن مسار إعادة المحاولة (نفس منطق UseMessageRetry(Immediate(5)) في الإنتاج) يعيد القراءة
// بأحدث حالة ويحسم إمّا بالنجاح أو برفض Business صحيح ("مخزون غير كافٍ")، بلا فقدان تحديث.
//
// نُشغّل العمليات على مستوى الـ DbContext/الـ repository مباشرة (الطبقة التي يُفرض فيها xmin فعلياً)،
// وهذا مُصرّح به في التعليمات عند تعذّر تشغيل غلاف إعادة المحاولة الخاص بـ MassTransit غير تفاعلياً.
public class StockItemConcurrencyTests
{
    // نفس سلسلة اتصال خدمة الـ Inventory (Postgres على 5433 من docker compose).
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=inventorydb;Username=postgres;Password=postgres";

    // عدد مرات إعادة المحاولة الفورية — مطابق لإعداد الإنتاج Immediate(5) على نقطة استقبال الـ Inventory.
    private const int MaxImmediateRetries = 5;

    private readonly ITestOutputHelper _output;

    public StockItemConcurrencyTests(ITestOutputHelper output) => _output = output;

    private static InventoryDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    // تهيئة صف اختبار معزول بمعرّف منتج فريد لكل تشغيلة، وتنظيفه بعدها.
    private static async Task<Guid> SeedStockAsync(int available)
    {
        var productId = Guid.NewGuid();
        await using var ctx = NewContext();
        ctx.StockItems.Add(StockItem.Create(productId, available));
        await ctx.SaveChangesAsync();
        return productId;
    }

    private static async Task CleanupAsync(Guid productId)
    {
        await using var ctx = NewContext();
        var item = await ctx.StockItems.FirstOrDefaultAsync(s => s.ProductId == productId);
        if (item is not null)
        {
            ctx.StockItems.Remove(item);
            await ctx.SaveChangesAsync();
        }
    }

    // ----------------------------------------------------------------------------------
    // الحالة 1: السباق الحتمي يُفشل الكاتب الخاسر بـ DbUpdateConcurrencyException،
    //           وإعادة المحاولة تعيد القراءة وتنجح لأن المخزون كافٍ لكلا العمليتين.
    //           المحصلة: لا فقدان تحديث — الحجزان ينعكسان، والمجموع محفوظ.
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task Concurrent_reserve_loser_hits_concurrency_exception_then_retry_resolves_when_stock_sufficient()
    {
        const int initial = 100;
        const int qty = 10;
        var productId = await SeedStockAsync(initial);

        try
        {
            // كلا السياقين يقرآن نفس الصف => نفس لقطة xmin. هذا يضمن التصادم في كل تشغيلة (ليس توقيتاً).
            await using var ctxA = NewContext();
            await using var ctxB = NewContext();

            var itemA = await ctxA.StockItems.FirstAsync(s => s.ProductId == productId);
            var itemB = await ctxB.StockItems.FirstAsync(s => s.ProductId == productId);

            var xminA = ctxA.Entry(itemA).Property<uint>("xmin").CurrentValue;
            var xminB = ctxB.Entry(itemB).Property<uint>("xmin").CurrentValue;
            Assert.Equal(xminA, xminB); // كلاهما يرى نفس إصدار الصف قبل أي التزام

            // الفائز يحجز ويلتزم أولاً => تتغيّر xmin في قاعدة البيانات.
            Assert.True(itemA.Reserve(qty).Succeeded);
            await ctxA.SaveChangesAsync();

            // الخاسر يحاول الحفظ بنفس xmin القديمة => يجب أن يفشل حتماً.
            Assert.True(itemB.Reserve(qty).Succeeded);
            var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
            _output.WriteLine($"[CASE1] Loser threw DbUpdateConcurrencyException as expected: {ex.Message}");

            // مسار إعادة المحاولة: اقرأ أحدث صف وأعد تطبيق الحجز (نفس حلّ الإنتاج).
            var (succeeded, reason, retries) = await RetryReserveAsync(productId, qty);
            _output.WriteLine($"[CASE1] Retry resolved: succeeded={succeeded}, reason={reason}, retries={retries}");
            Assert.True(succeeded);
            Assert.True(retries >= 1); // أعاد القراءة فعلاً بعد التصادم

            // الثوابت النهائية: لا فقدان تحديث — حجزان بإجمالي 20، والمجموع (المتاح + المحجوز) ثابت.
            await using var verify = NewContext();
            var final = await verify.StockItems.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(initial - (2 * qty), final.AvailableQuantity); // 80
            Assert.Equal(2 * qty, final.ReservedQuantity);              // 20
            Assert.Equal(initial, final.AvailableQuantity + final.ReservedQuantity); // المجموع محفوظ = 100
        }
        finally
        {
            await CleanupAsync(productId);
        }
    }

    // ----------------------------------------------------------------------------------
    // الحالة 2: نفس التصادم الحتمي، لكن المخزون يكفي لعملية واحدة فقط.
    //           الفائز يحجز، والخاسر يفشل بـ DbUpdateConcurrencyException، وعند إعادة القراءة
    //           يجد مخزوناً غير كافٍ => يُحسم برفض Business صحيح (Result)، لا باستثناء ولا بفقدان تحديث.
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task Concurrent_reserve_loser_retry_resolves_to_business_rejection_when_stock_insufficient()
    {
        const int initial = 10;
        const int qty = 7; // 7 + 7 = 14 > 10 => واحد فقط يمكن أن ينجح
        var productId = await SeedStockAsync(initial);

        try
        {
            await using var ctxA = NewContext();
            await using var ctxB = NewContext();

            var itemA = await ctxA.StockItems.FirstAsync(s => s.ProductId == productId);
            var itemB = await ctxB.StockItems.FirstAsync(s => s.ProductId == productId);

            Assert.Equal(
                ctxA.Entry(itemA).Property<uint>("xmin").CurrentValue,
                ctxB.Entry(itemB).Property<uint>("xmin").CurrentValue);

            Assert.True(itemA.Reserve(qty).Succeeded);
            await ctxA.SaveChangesAsync();

            Assert.True(itemB.Reserve(qty).Succeeded);
            var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
            _output.WriteLine($"[CASE2] Loser threw DbUpdateConcurrencyException as expected: {ex.Message}");

            // إعادة المحاولة تحسم ضد الحالة الفعلية بعد الفائز (المتاح الآن = 3 < 7) => رفض Business.
            var (succeeded, reason, retries) = await RetryReserveAsync(productId, qty);
            _output.WriteLine($"[CASE2] Retry resolved: succeeded={succeeded}, reason={reason}, retries={retries}");
            Assert.False(succeeded);
            Assert.Equal("Insufficient available quantity.", reason);
            Assert.True(retries >= 1);

            // الثوابت: حجز واحد فقط نجح، لا فقدان ولا إفراط في العدّ، المجموع محفوظ.
            await using var verify = NewContext();
            var final = await verify.StockItems.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(initial - qty, final.AvailableQuantity); // 3
            Assert.Equal(qty, final.ReservedQuantity);            // 7
            Assert.Equal(initial, final.AvailableQuantity + final.ReservedQuantity); // = 10
        }
        finally
        {
            await CleanupAsync(productId);
        }
    }

    // ----------------------------------------------------------------------------------
    // اختبار حارس: يُثبت أن الآلية ليست no-op. لو لم تكن xmin مفعّلة كـ concurrency token،
    // لكان الحفظ الثاني "الأعمى" (بسياق منفصل بنفس القراءة القديمة) قد نجح ودهس تحديث الفائز
    // (آخر كاتب يفوز = فقدان تحديث). نُثبت هنا أن EF يرفع الاستثناء فعلاً، أي أن الحماية حقيقية.
    // ----------------------------------------------------------------------------------
    [Fact]
    public async Task Naive_last_writer_wins_is_prevented_by_concurrency_token()
    {
        const int initial = 50;
        var productId = await SeedStockAsync(initial);

        try
        {
            await using var ctxA = NewContext();
            await using var ctxB = NewContext();

            var itemA = await ctxA.StockItems.FirstAsync(s => s.ProductId == productId);
            var itemB = await ctxB.StockItems.FirstAsync(s => s.ProductId == productId);

            itemA.Reserve(5);
            await ctxA.SaveChangesAsync(); // الفائز: المتاح 45

            // الكاتب الأعمى يحاول الكتابة فوق حالة قديمة. بدون الـ token كان سينجح ويعيد المتاح إلى 42
            // (فيضيع حجز الفائز). مع الـ token يُرفَض بـ DbUpdateConcurrencyException.
            itemB.Reserve(8);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());

            await using var verify = NewContext();
            var final = await verify.StockItems.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(45, final.AvailableQuantity); // حجز الفائز محفوظ، لم يُدهَس
            Assert.Equal(5, final.ReservedQuantity);
        }
        finally
        {
            await CleanupAsync(productId);
        }
    }

    // يحاكي بدقة منطق إعادة المحاولة في الإنتاج: عند DbUpdateConcurrencyException أعد القراءة بأحدث
    // قيمة xmin من سياق جديد وأعد تطبيق الحجز، حتى MaxImmediateRetries مرات. لا يُضعف الآلية إطلاقاً —
    // كل محاولة تحسم ضد الحالة الفعلية الأحدث؛ فإن لم يعد المخزون كافياً تعيد نتيجة رفض Business.
    private static async Task<(bool succeeded, string? reason, int retries)> RetryReserveAsync(Guid productId, int quantity)
    {
        for (var attempt = 1; attempt <= MaxImmediateRetries; attempt++)
        {
            await using var ctx = NewContext();
            var item = await ctx.StockItems.FirstAsync(s => s.ProductId == productId);

            var result = item.Reserve(quantity);
            if (!result.Succeeded)
                return (false, result.Reason, attempt); // رفض Business ضد أحدث حالة

            try
            {
                await ctx.SaveChangesAsync();
                return (true, null, attempt);
            }
            catch (DbUpdateConcurrencyException)
            {
                // تصادم آخر خلال إعادة المحاولة => كرّر بأحدث قراءة.
                if (attempt == MaxImmediateRetries) throw;
            }
        }

        throw new InvalidOperationException("Retry loop exhausted without resolution.");
    }
}
