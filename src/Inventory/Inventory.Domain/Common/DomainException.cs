// src/Inventory/Inventory.Domain/Common/DomainException.cs
namespace Inventory.Domain.Common;

public class DomainException(string message) : Exception(message);