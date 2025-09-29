# Telegram Support Bot

Описание и инструкция по запуску Telegram-бота, который собирает пересланные сообщения/ручные обращения и сохраняет их в Google Sheets.

---

## Краткое описание

Бот работает в режиме long polling, принимает сообщения из чатов, группирует пересланные сообщения в "кейс", запрашивает у оператора имя и записывает кейс в указанную Google таблицу. Реализована простая retry-логика для обращения к Google Sheets и защита от гонок при группировке сообщений.

---

## Структура README

* Требования
* Настройка (конфигурация и секреты)
* Запуск локально
* Docker (опционно)
* Формат Google Sheets
* Переменные конфигурации
* Полезные заметки по отладке и безопасности

---

## Требования

* .NET 7+ SDK
* Аккаунт в Google с доступом к Google Sheets API (service account или OAuth credentials). Рекомендуется использовать service account.
* Telegram-бот (токен от BotFather)

---

## Конфигурация

Проект читает конфигурацию через `IConfiguration` (appsettings, переменные окружения и user-secrets в Development).

### Пример `appsettings.json`

```json
{
  "BotConfiguration": {
    "BotToken": "<TELEGRAM_BOT_TOKEN>",
    "SpreadsheetId": "<GOOGLE_SHEET_ID>"
  },
  "Google": {
    "CredentialsPath": "./credentials.json"
  }
}
```

**Замечание:** в `Development` окружении проект использует `dotnet user-secrets` (включено в `Program.Main`).

### Как хранить секреты

* **Локально (рекомендуется для разработки):** используйте `dotnet user-secrets`:

```bash
dotnet user-secrets init
dotnet user-secrets set "BotConfiguration:BotToken" "<TELEGRAM_BOT_TOKEN>"
dotnet user-secrets set "BotConfiguration:SpreadsheetId" "<SPREADSHEET_ID>"
dotnet user-secrets set "Google:CredentialsPath" "C:\path\to\service-account.json"
```

* **В продакшене:** используйте переменные окружения (например в systemd unit, Docker или в облачном провайдере) или секрет-менеджер вашего окружения.

Переменные окружения имеет приоритет и могут выглядеть так (пример для Linux):

```bash
export BotConfiguration__BotToken="<TELEGRAM_BOT_TOKEN>"
export BotConfiguration__SpreadsheetId="<SPREADSHEET_ID>"
export Google__CredentialsPath="/app/credentials.json"
```

---

## Google API: настройка service account (рекомендуется)

1. Откройте Google Cloud Console и создайте проект (или используйте существующий).
2. Включите API: **Google Sheets API**.
3. Создайте Service Account и скачайте JSON с ключом.
4. Поделитесь Google таблицей (Spreadsheet) с email-ом service account (role `Editor` или `Viewer` + возможность добавлять строки, рекомендуется `Editor`).
5. Поместите JSON-файл в путь, указанный в `Google:CredentialsPath`. Для Docker-контейнера — смонтируйте его.

---

## Формат Google Sheets

Таблица ожидает лист с именем `Лист1` (можно изменить в константе `GoogleSheetsHelper.SheetName`). Колонки записываются в диапазон A..J и содержат:

| Колонка | Описание                                |
| ------- | --------------------------------------- |
| A       | Case ID (сгенерированный)               |
| B       | Timestamp (дата/время)                  |
| C       | (резерв)                                |
| D       | (резерв)                                |
| E       | Оператор                                |
| F       | Клиент (информация об отправителе)      |
| G       | Текст проблемы (объединённые сообщения) |
| H       | Статус                                  |
| I       | Флаг (boolean)                          |
| J       | Флаг (boolean)                          |

Максимальная проверяемая строка ограничена в коде константой `MaxDataRow = 199`. При заполнении до этой строки попытка записи бросит исключение. При необходимости измените константу.

---

## Запуск локально

1. Сконфигурируйте секреты/`appsettings.json` (см. разделы выше).
2. Соберите и запустите приложение:

```bash
dotnet build
dotnet run --project <path-to-csproj>
```

Либо используйте `dotnet run` из каталога проекта.

---

## Docker (базовый пример)

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish ./
# Скопируйте credentials.json в контейнер или смонтируйте volume
# COPY credentials.json ./credentials.json
ENV BotConfiguration__BotToken=""
ENV BotConfiguration__SpreadsheetId=""
ENV Google__CredentialsPath="/app/credentials.json"
ENTRYPOINT ["dotnet", "YourAssemblyName.dll"]
```

При запуске контейнера рекомендуется **не** класть ключи прямо в образ — используйте `docker run -v /local/path/credentials.json:/app/credentials.json`.

---

## Отладка и распространённые проблемы

* **Google API: доступ запрещён** — проверьте, что вы включили Sheets API и что spreadsheet доступен для service account (share the spreadsheet with service account email).
* **401/403 при чтении/записи** — убедитесь, что `CredentialsPath` указывает на корректный JSON и что ключ активен.
* **Таблица заполнена** — при ошибке, указывающей на заполнение до `MaxDataRow`, увеличьте `MaxDataRow` или очистите/добавьте новый лист.
* **Токен бота неверен** — проверьте, что `BotToken` от BotFather корректен и бот активирован.
* **Перегрузка** — если много чатов и кейсов, контролируйте константу `MAX_PENDING_CASES`.
* **Логи** — приложение использует `ILogger`. Для локальной разработки изменяйте уровни логирования через `appsettings.Development.json` или переменные окружения.

---

## Безопасность

* Никогда не храните секреты в публичных репозиториях.
* Для production используйте защищённый хранилище секретов (Vault, Azure Key Vault, AWS Secrets Manager и т.п.).
* Для service account ограничьте права в Google Cloud Platform по необходимости.

---

Автор: ShadowEagleDev
