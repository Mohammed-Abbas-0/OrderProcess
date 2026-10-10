-- إنشاء قواعد البيانات الأربع المنطقية داخل مثيل PostgreSQL الواحد.
-- كل سياق محدود (bounded context) يملك قاعدة بيانات خاصة به، بالإضافة إلى قاعدة الـ Saga.
-- يُنفَّذ هذا السكربت تلقائياً عند أول إقلاع للحاوية عبر /docker-entrypoint-initdb.d.
CREATE DATABASE orderingdb OWNER postgres;
CREATE DATABASE inventorydb OWNER postgres;
CREATE DATABASE paymentdb OWNER postgres;
CREATE DATABASE sagadb OWNER postgres;
