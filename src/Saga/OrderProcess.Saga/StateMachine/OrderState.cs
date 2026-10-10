using MassTransit;

namespace OrderProcess.Saga.StateMachine;

// حالة نسخة الـ Saga المحفوظة في قاعدة بيانات sagadb.
// CorrelationId هو مفتاح الربط (نعتمد اصطلاح CorrelationId = OrderId) وهو مفتاح الجدول الأساسي.
public class OrderState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }

    // الحالة الحالية للـ state machine (Submitted/StockReserved/Paid/...).
    public string CurrentState { get; set; } = default!;

    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }

    public DateTime SubmittedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // رمز التحكم بالتزامن المتفائل (Optimistic Concurrency) لنسخة الـ Saga.
    public uint RowVersion { get; set; }
}
