# MSSQL to PostgreSQL Migration Worker

## Türkçe

Bu proje, MSSQL veritabanındaki verileri PostgreSQL veritabanına aktarmak ve senkronize etmek için geliştirilmiş bir .NET 8 Worker Service uygulamasıdır.

Projede:
- 5 farklı tablo arasında veri aktarımı yapılmaktadır.
- MSSQL ve PostgreSQL tarafında tablo ve kolon isimleri farklıdır.
- `UpdatedAt` ve `sync_state` kullanılarak yalnızca yeni veya değişen kayıtlar senkronize edilir.
- PostgreSQL tarafında `ON CONFLICT DO UPDATE` ile duplicate kayıt oluşması engellenir.
- Worker Service 30 saniyede bir çalışarak verileri kontrol eder.
- Foreign Key ilişkilerine uygun aktarım sırası kullanılır.

### Kullanılan Teknolojiler

- C#
- .NET 8
- Worker Service
- MSSQL
- PostgreSQL
- Microsoft.Data.SqlClient
- Npgsql
- Async / Await
- Windows Service

---

## English

This project is a .NET 8 Worker Service developed to migrate and synchronize data from MSSQL to PostgreSQL.

It supports:
- Data migration between 5 related tables
- Different table and column mappings
- Incremental synchronization using `UpdatedAt`
- Sync state tracking
- PostgreSQL upsert with `ON CONFLICT DO UPDATE`
- Foreign key aware migration order
- Periodic background synchronization
- Windows Service support
