# افزونه‌های نوار بالای شمسا

هر فایل `*.json` در پوشه `%LocalAppData%\Shamsa\plugins` یک جزء به نوار بالا اضافه می‌کند.
افزونه‌ها فقط وقتی اجرا می‌شوند که در «استودیو دسکتاپ ← افزونه‌ها» روشن باشند.

## ساختار فایل

```json
{
  "id": "my-plugin",
  "title": "عنوان (در راهنمای موس دیده می‌شود)",
  "icon": "",
  "text": "متن ثابت (اگر source نباشد)",
  "source": { "type": "command", "value": "hostname", "intervalSeconds": 60 },
  "onClick": { "type": "open", "value": "ms-settings:about" },
  "position": "right"
}
```

| فیلد | توضیح |
|---|---|
| `icon` | یک نماد Segoe Fluent Icons (مثل ``) یا یک ایموجی |
| `source.type` | `builtin` (مقدار: `uptime`، `localip`، `date`) — `command` — `file` — `http` |
| `source.value` | فرمان، مسیر فایل (متغیرهایی مثل `%USERPROFILE%` مجازند) یا نشانی وب |
| `source.intervalSeconds` | فاصله به‌روزرسانی؛ حداقل ۱۰ ثانیه |
| `onClick.type` | `open` (نشانی، فایل، `ms-settings:`) یا `command` |
| `position` | `left`، `center` یا `right` |

## قوانین ایمنی

- فرمان‌ها بدون پنجره کنسول و حداکثر ۵ ثانیه اجرا می‌شوند؛ فقط خط اول خروجی (تا ۴۰ نویسه) نمایش داده می‌شود.
- `http` فقط درخواست GET به نشانی‌های `http`/`https` با مهلت ۵ ثانیه است.
- فرمان‌ها با دسترسی حساب کاربری شما اجرا می‌شوند؛ فقط افزونه‌هایی را بگذارید که محتوایشان را می‌شناسید.

## نمونه‌ها

نسخه ویندوز:

```json
{ "id": "winver", "title": "نام رایانه", "icon": "",
  "source": { "type": "command", "value": "hostname", "intervalSeconds": 3600 },
  "position": "left" }
```

خط اول یک فایل یادداشت:

```json
{ "id": "todo", "title": "کار امروز", "icon": "📝",
  "source": { "type": "file", "value": "%USERPROFILE%\\Desktop\\today.txt", "intervalSeconds": 30 },
  "onClick": { "type": "open", "value": "%USERPROFILE%\\Desktop\\today.txt" } }
```

در اولین اجرا دو نمونه بی‌خطر (`uptime.json` و `local-ip.json`) ساخته می‌شود.
