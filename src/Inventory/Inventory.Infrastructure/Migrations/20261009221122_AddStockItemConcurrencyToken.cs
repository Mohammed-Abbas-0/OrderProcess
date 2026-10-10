using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockItemConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // لا يوجد DDL فعليّ: xmin عمود نظام (system column) موجود أصلاً في كل جدول Postgres،
            // ونحن فقط نعيّنه كـ concurrency token على مستوى نموذج EF (shadow property).
            // لذلك هذه الـ migration مقصودة أن تكون بلا تغيير فعليّ على المخطط؛ وجودها يُبقي
            // الـ ModelSnapshot متوافقاً مع النموذج (مطابقة xmin في StockItems).
            // (AddColumn التلقائي محذوف عمداً لأنه سيفشل: لا يمكن إضافة عمود النظام xmin يدوياً.)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // لا شيء نتراجع عنه: لم نُنشئ عموداً فعلياً (xmin عمود نظام).
        }
    }
}
