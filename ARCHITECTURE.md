# Архитектура

## Нужны ли папки Gps / Interbase / Video / Shared / MbWebApp?

**Да, все нужны** — это не «лишние сервисы», а части одного приложения:

| Проект | Роль |
|--------|------|
| **MbWebApp** | Главный exe: UI + хост всех модулей (:5555) |
| **Nomeroff.Shared** | Общий код (`PlateAlphabet`) |
| **Nomeroff.Gps.Api** | Библиотека GPS (COM) |
| **Nomeroff.Interbase.Api** | Библиотека записи в БД |
| **Nomeroff.Video.Api** | Библиотека ffmpeg→OCR |
| **nomeroff-net** | Отдельный процесс Python OCR (:8000) |

Отдельные exe Gps/Interbase/Video больше **не запускаются**.

WASM-клиент убран из solution (папка может остаться, но не используется).

## Запуск

`D:\_ANumberRecognition\start-app.bat` → 2 процесса.
