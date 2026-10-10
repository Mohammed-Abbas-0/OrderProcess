using System.Collections.Concurrent;
using MassTransit;

namespace BuildingBlocks.Observability;

// مراقِب استهلاك يدفع CorrelationId الخاص بالرسالة إلى سياق Serilog (LogContext) طوال فترة المعالجة،
// ثم يزيله بعد الانتهاء. بذلك تحمل كل أسطر السجل الصادرة أثناء الـ Consume نفس CorrelationId المستخدَم
// في تدفّق HTTP، فيصبح تدفّق طلب واحد قابلاً للترشيح بمعرّف واحد عبر كل الخدمات.
//
// نحتفظ بالـ IDisposable الناتج عن LogContext.PushProperty في قاموس مُفهرَس بالـ ConsumeContext،
// لأن الدفع يحدث في PreConsume والإزالة في PostConsume/ConsumeFault (نطاقان منفصلان).
public sealed class CorrelationIdLogObserver : IConsumeObserver
{
    private readonly ConcurrentDictionary<ConsumeContext, IDisposable> _scopes = new();

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        // CorrelationId من مظروف الرسالة؛ وإن غاب نحاول أخذه من MessageId كبديل.
        var correlationId = context.CorrelationId?.ToString()
                            ?? context.MessageId?.ToString()
                            ?? "none";

        _scopes[context] = Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId);
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
    {
        if (_scopes.TryRemove(context, out var scope))
            scope.Dispose();
        return Task.CompletedTask;
    }

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        if (_scopes.TryRemove(context, out var scope))
            scope.Dispose();
        return Task.CompletedTask;
    }
}
