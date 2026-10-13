using MassTransit;

namespace OrderProcess.Saga.StateMachine;

public class OrderState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
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
