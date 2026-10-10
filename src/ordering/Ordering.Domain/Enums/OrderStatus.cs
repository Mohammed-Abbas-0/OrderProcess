namespace Ordering.Domain.Enums;

// Rejected حالة نهائية لفشل عمل (Business) لا يتطلّب تعويضاً (مثل فشل حجز المخزون قبل أي حجز)،
// وهي متمايزة عن Cancelled التي تمثّل إلغاءً بعد تعويض (فشل الدفع بعد نجاح الحجز).
public enum OrderStatus { Draft, Submitted, Confirmed, Cancelled, Rejected }
