// src/Payment/Payment.Domain/Common/DomainException.cs
namespace Payment.Domain.Common;
public class DomainException(string message) : Exception(message);