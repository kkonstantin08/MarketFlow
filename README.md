# MarketFlow

MarketFlow — система учёта товаров и остатков на нескольких складах. Она предназначена для регистрации поступлений, резервирования единиц товара и просмотра истории операций.

Реализация MVP начата. Сейчас готовы основа API, Swagger UI и отдельная тестовая PostgreSQL; складские операции добавляются по этапам плана.

## Возможности MVP

- Создание и просмотр товаров и складов. Поиск товара по названию или SKU.
- Приёмка товара без повторного начисления по одному номеру поставки.
- Резервирование в пределах доступного количества.
- Просмотр фактических, зарезервированных и доступных остатков и журнала операций.
- Swagger UI для всех операций и одна демонстрационная страница для сценария приёмки и резерва.

Планируемый стек — C# и .NET 10, ASP.NET Core Web API, PostgreSQL, EF Core, OpenAPI со Swagger UI, Docker Compose и xUnit. Проверки в GitHub Actions запланированы на Linux. Один API и одна база обслуживают все складские операции. Подробнее в [архитектуре](docs/architecture.md).

## Разработка и проверки

Нужны .NET 10 SDK и Docker Compose. Если SDK установлен через `dotnet-install.sh`, добавьте `~/.dotnet` в `PATH` и задайте `DOTNET_ROOT=~/.dotnet`.

Запустите тестовую PostgreSQL и проверки:

```sh
docker compose -f compose.test.yaml up -d --wait
dotnet test MarketFlow.sln
```

Чтобы открыть текущую основу API локально:

```sh
dotnet run --project src/MarketFlow.Api --urls http://localhost:8080
```

Swagger UI доступен на `http://localhost:8080/swagger`. Для остановки тестовой БД выполните `docker compose -f compose.test.yaml down`.

Полный Compose-запуск API и БД появится на этапе 5.

## Примеры планируемого API

| Запрос | Назначение |
| --- | --- |
| `GET /api/products?q=BOX` | Поиск по названию или SKU. |
| `POST /api/supplies` с JSON `{"reference":"IN-001","productId":1,"warehouseId":1,"quantity":10}` | Принять 10 единиц. Повтор того же запроса не увеличивает остаток. |
| `POST /api/reservations` с JSON `{"productId":1,"warehouseId":1,"quantity":4}` | Зарезервировать 4 единицы. После приведённой приёмки останется 6 доступных. |
| `GET /api/stocks?warehouseId=1` | Посмотреть остатки по складу. |
| `GET /api/stock-movements?productId=1` | Посмотреть историю операций по товару. |

Примеры предполагают существующие товар и склад. Полный контракт и критерии приёмки — в [спецификации MVP](docs/specs/mvp.md). Остальные документы собраны в [указателе](docs/README.md).
