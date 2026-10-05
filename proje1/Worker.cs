using Microsoft.Data.SqlClient;
using Npgsql;
using proje1.Models;
using System.Data;

namespace proje1
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;

        public Worker(
            ILogger<Worker> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        // =========================================================
        // ANA WORKER
        // =========================================================

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation(
                        "Migration baþladý: {Time}",
                        DateTimeOffset.Now);

                    // Foreign Key sýrasýna dikkat ediyoruz.
                    await SyncCustomers(stoppingToken);
                    await SyncProducts(stoppingToken);
                    await SyncEmployees(stoppingToken);
                    await SyncOrders(stoppingToken);
                    await SyncPayments(stoppingToken);

                    _logger.LogInformation(
                        "Migration turu baþarýyla tamamlandý.");
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Migration sýrasýnda hata oluþtu.");
                }

                try
                {
                    _logger.LogInformation(
                        "30 saniye bekleniyor...");

                    await Task.Delay(
                        TimeSpan.FromSeconds(30),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }


        // =========================================================
        // CONNECTION STRING
        // =========================================================

        private string GetSqlServerConnectionString()
        {
            return _configuration
                .GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException(
                    "SqlServer connection string bulunamadý.");
        }

        private string GetPostgreSqlConnectionString()
        {
            return _configuration
                .GetConnectionString("PostgreSql")
                ?? throw new InvalidOperationException(
                    "PostgreSql connection string bulunamadý.");
        }


        // =========================================================
        // SYNC STATE
        // =========================================================

        private async Task<DateTime> GetLastSyncTime(
            string tableName,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                SELECT last_sync_time
                FROM sync_state
                WHERE table_name = @table_name;";

            await using NpgsqlCommand command =
                new NpgsqlCommand(query, connection);

            command.Parameters.AddWithValue(
                "table_name",
                tableName);

            object? result =
                await command.ExecuteScalarAsync(stoppingToken);

            // Ýlk migration ise sync_state içinde kayýt olmayacak.
            // SQL Server DATETIME2 0001 tarihini desteklediði için
            // DateTime.MinValue kullanabiliriz.
            if (result == null || result == DBNull.Value)
            {
                return DateTime.MinValue;
            }

            return Convert.ToDateTime(result);
        }


        private async Task UpdateLastSyncTime(
            string tableName,
            DateTime lastSyncTime,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                INSERT INTO sync_state
                (
                    table_name,
                    last_sync_time
                )
                VALUES
                (
                    @table_name,
                    @last_sync_time
                )
                ON CONFLICT (table_name)
                DO UPDATE SET
                    last_sync_time = EXCLUDED.last_sync_time;";

            await using NpgsqlCommand command =
                new NpgsqlCommand(query, connection);

            command.Parameters.AddWithValue(
                "table_name",
                tableName);

            command.Parameters.AddWithValue(
                "last_sync_time",
                lastSyncTime);

            await command.ExecuteNonQueryAsync(stoppingToken);
        }


        // =========================================================
        // CUSTOMERS -> CLIENTS
        // =========================================================

        private async Task SyncCustomers(
            CancellationToken stoppingToken)
        {
            DateTime lastSync =
                await GetLastSyncTime(
                    "Customers",
                    stoppingToken);

            List<Customer> customers =
                await GetCustomersFromSqlServer(
                    lastSync,
                    stoppingToken);

            if (customers.Count == 0)
            {
                _logger.LogInformation(
                    "Customers: yeni veya güncellenmiþ kayýt yok.");

                return;
            }

            await SaveCustomersToPostgreSql(
                customers,
                stoppingToken);

            DateTime newestUpdate =
                customers.Max(x => x.UpdatedAt);

            await UpdateLastSyncTime(
                "Customers",
                newestUpdate,
                stoppingToken);

            _logger.LogInformation(
                "Customers: {Count} kayýt senkronize edildi.",
                customers.Count);
        }


        private async Task<List<Customer>>
            GetCustomersFromSqlServer(
                DateTime lastSync,
                CancellationToken stoppingToken)
        {
            List<Customer> customers = new();

            await using SqlConnection connection =
                new SqlConnection(
                    GetSqlServerConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                SELECT
                    CustomerId,
                    FirstName,
                    LastName,
                    Email,
                    Phone,
                    CreatedAt,
                    UpdatedAt
                FROM dbo.Customers
                WHERE UpdatedAt > @LastSyncTime
                ORDER BY UpdatedAt;";

            await using SqlCommand command =
                new SqlCommand(query, connection);

            // ÖNEMLÝ:
            // AddWithValue yerine DateTime2 tipini açýkça belirtiyoruz.
            // Böylece 01.01.0001 deðeri SqlDateTime overflow oluþturmaz.
            command.Parameters
                .Add("@LastSyncTime", SqlDbType.DateTime2)
                .Value = lastSync;

            await using SqlDataReader reader =
                await command.ExecuteReaderAsync(stoppingToken);

            while (await reader.ReadAsync(stoppingToken))
            {
                Customer customer = new Customer
                {
                    CustomerId = reader.GetInt32(0),
                    FirstName = reader.GetString(1),
                    LastName = reader.GetString(2),
                    Email = reader.GetString(3),

                    Phone = reader.IsDBNull(4)
                        ? null
                        : reader.GetString(4),

                    CreatedAt = reader.GetDateTime(5),
                    UpdatedAt = reader.GetDateTime(6)
                };

                customers.Add(customer);
            }

            return customers;
        }


        private async Task SaveCustomersToPostgreSql(
            List<Customer> customers,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            foreach (Customer customer in customers)
            {
                const string query = @"
                    INSERT INTO clients
                    (
                        client_id,
                        name,
                        surname,
                        email_address,
                        phone_number,
                        created_at,
                        modified_at
                    )
                    VALUES
                    (
                        @client_id,
                        @name,
                        @surname,
                        @email,
                        @phone,
                        @created_at,
                        @modified_at
                    )
                    ON CONFLICT (client_id)
                    DO UPDATE SET
                        name = EXCLUDED.name,
                        surname = EXCLUDED.surname,
                        email_address = EXCLUDED.email_address,
                        phone_number = EXCLUDED.phone_number,
                        created_at = EXCLUDED.created_at,
                        modified_at = EXCLUDED.modified_at;";

                await using NpgsqlCommand command =
                    new NpgsqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "client_id",
                    customer.CustomerId);

                command.Parameters.AddWithValue(
                    "name",
                    customer.FirstName);

                command.Parameters.AddWithValue(
                    "surname",
                    customer.LastName);

                command.Parameters.AddWithValue(
                    "email",
                    customer.Email);

                command.Parameters.AddWithValue(
                    "phone",
                    (object?)customer.Phone ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "created_at",
                    customer.CreatedAt);

                command.Parameters.AddWithValue(
                    "modified_at",
                    customer.UpdatedAt);

                await command.ExecuteNonQueryAsync(stoppingToken);
            }
        }


        // =========================================================
        // PRODUCTS -> ITEMS
        // =========================================================

        private async Task SyncProducts(
            CancellationToken stoppingToken)
        {
            DateTime lastSync =
                await GetLastSyncTime(
                    "Products",
                    stoppingToken);

            List<Product> products =
                await GetProductsFromSqlServer(
                    lastSync,
                    stoppingToken);

            if (products.Count == 0)
            {
                _logger.LogInformation(
                    "Products: yeni veya güncellenmiþ kayýt yok.");

                return;
            }

            await SaveProductsToPostgreSql(
                products,
                stoppingToken);

            DateTime newestUpdate =
                products.Max(x => x.UpdatedAt);

            await UpdateLastSyncTime(
                "Products",
                newestUpdate,
                stoppingToken);

            _logger.LogInformation(
                "Products: {Count} kayýt senkronize edildi.",
                products.Count);
        }


        private async Task<List<Product>>
            GetProductsFromSqlServer(
                DateTime lastSync,
                CancellationToken stoppingToken)
        {
            List<Product> products = new();

            await using SqlConnection connection =
                new SqlConnection(
                    GetSqlServerConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                SELECT
                    ProductId,
                    ProductName,
                    Category,
                    Price,
                    StockQuantity,
                    CreatedAt,
                    UpdatedAt
                FROM dbo.Products
                WHERE UpdatedAt > @LastSyncTime
                ORDER BY UpdatedAt;";

            await using SqlCommand command =
                new SqlCommand(query, connection);

            command.Parameters
                .Add("@LastSyncTime", SqlDbType.DateTime2)
                .Value = lastSync;

            await using SqlDataReader reader =
                await command.ExecuteReaderAsync(stoppingToken);

            while (await reader.ReadAsync(stoppingToken))
            {
                Product product = new Product
                {
                    ProductId = reader.GetInt32(0),
                    ProductName = reader.GetString(1),
                    Category = reader.GetString(2),
                    Price = reader.GetDecimal(3),
                    StockQuantity = reader.GetInt32(4),
                    CreatedAt = reader.GetDateTime(5),
                    UpdatedAt = reader.GetDateTime(6)
                };

                products.Add(product);
            }

            return products;
        }


        private async Task SaveProductsToPostgreSql(
            List<Product> products,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            foreach (Product product in products)
            {
                const string query = @"
                    INSERT INTO items
                    (
                        item_id,
                        item_name,
                        category_name,
                        unit_price,
                        stock_count,
                        created_at,
                        modified_at
                    )
                    VALUES
                    (
                        @item_id,
                        @item_name,
                        @category_name,
                        @unit_price,
                        @stock_count,
                        @created_at,
                        @modified_at
                    )
                    ON CONFLICT (item_id)
                    DO UPDATE SET
                        item_name = EXCLUDED.item_name,
                        category_name = EXCLUDED.category_name,
                        unit_price = EXCLUDED.unit_price,
                        stock_count = EXCLUDED.stock_count,
                        created_at = EXCLUDED.created_at,
                        modified_at = EXCLUDED.modified_at;";

                await using NpgsqlCommand command =
                    new NpgsqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "item_id",
                    product.ProductId);

                command.Parameters.AddWithValue(
                    "item_name",
                    product.ProductName);

                command.Parameters.AddWithValue(
                    "category_name",
                    product.Category);

                command.Parameters.AddWithValue(
                    "unit_price",
                    product.Price);

                command.Parameters.AddWithValue(
                    "stock_count",
                    product.StockQuantity);

                command.Parameters.AddWithValue(
                    "created_at",
                    product.CreatedAt);

                command.Parameters.AddWithValue(
                    "modified_at",
                    product.UpdatedAt);

                await command.ExecuteNonQueryAsync(stoppingToken);
            }
        }


        // =========================================================
        // EMPLOYEES -> STAFF_MEMBERS
        // =========================================================

        private async Task SyncEmployees(
            CancellationToken stoppingToken)
        {
            DateTime lastSync =
                await GetLastSyncTime(
                    "Employees",
                    stoppingToken);

            List<Employee> employees =
                await GetEmployeesFromSqlServer(
                    lastSync,
                    stoppingToken);

            if (employees.Count == 0)
            {
                _logger.LogInformation(
                    "Employees: yeni veya güncellenmiþ kayýt yok.");

                return;
            }

            await SaveEmployeesToPostgreSql(
                employees,
                stoppingToken);

            DateTime newestUpdate =
                employees.Max(x => x.UpdatedAt);

            await UpdateLastSyncTime(
                "Employees",
                newestUpdate,
                stoppingToken);

            _logger.LogInformation(
                "Employees: {Count} kayýt senkronize edildi.",
                employees.Count);
        }


        private async Task<List<Employee>>
            GetEmployeesFromSqlServer(
                DateTime lastSync,
                CancellationToken stoppingToken)
        {
            List<Employee> employees = new();

            await using SqlConnection connection =
                new SqlConnection(
                    GetSqlServerConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                SELECT
                    EmployeeId,
                    FirstName,
                    LastName,
                    Department,
                    Email,
                    CreatedAt,
                    UpdatedAt
                FROM dbo.Employees
                WHERE UpdatedAt > @LastSyncTime
                ORDER BY UpdatedAt;";

            await using SqlCommand command =
                new SqlCommand(query, connection);

            command.Parameters
                .Add("@LastSyncTime", SqlDbType.DateTime2)
                .Value = lastSync;

            await using SqlDataReader reader =
                await command.ExecuteReaderAsync(stoppingToken);

            while (await reader.ReadAsync(stoppingToken))
            {
                Employee employee = new Employee
                {
                    EmployeeId = reader.GetInt32(0),
                    FirstName = reader.GetString(1),
                    LastName = reader.GetString(2),
                    Department = reader.GetString(3),
                    Email = reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5),
                    UpdatedAt = reader.GetDateTime(6)
                };

                employees.Add(employee);
            }

            return employees;
        }


        private async Task SaveEmployeesToPostgreSql(
            List<Employee> employees,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            foreach (Employee employee in employees)
            {
                const string query = @"
                    INSERT INTO staff_members
                    (
                        staff_id,
                        first_name,
                        last_name,
                        department_name,
                        email_address,
                        created_at,
                        modified_at
                    )
                    VALUES
                    (
                        @staff_id,
                        @first_name,
                        @last_name,
                        @department_name,
                        @email_address,
                        @created_at,
                        @modified_at
                    )
                    ON CONFLICT (staff_id)
                    DO UPDATE SET
                        first_name = EXCLUDED.first_name,
                        last_name = EXCLUDED.last_name,
                        department_name = EXCLUDED.department_name,
                        email_address = EXCLUDED.email_address,
                        created_at = EXCLUDED.created_at,
                        modified_at = EXCLUDED.modified_at;";

                await using NpgsqlCommand command =
                    new NpgsqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "staff_id",
                    employee.EmployeeId);

                command.Parameters.AddWithValue(
                    "first_name",
                    employee.FirstName);

                command.Parameters.AddWithValue(
                    "last_name",
                    employee.LastName);

                command.Parameters.AddWithValue(
                    "department_name",
                    employee.Department);

                command.Parameters.AddWithValue(
                    "email_address",
                    employee.Email);

                command.Parameters.AddWithValue(
                    "created_at",
                    employee.CreatedAt);

                command.Parameters.AddWithValue(
                    "modified_at",
                    employee.UpdatedAt);

                await command.ExecuteNonQueryAsync(stoppingToken);
            }
        }


        // =========================================================
        // ORDERS -> PURCHASES
        // =========================================================

        private async Task SyncOrders(
            CancellationToken stoppingToken)
        {
            DateTime lastSync =
                await GetLastSyncTime(
                    "Orders",
                    stoppingToken);

            List<Order> orders =
                await GetOrdersFromSqlServer(
                    lastSync,
                    stoppingToken);

            if (orders.Count == 0)
            {
                _logger.LogInformation(
                    "Orders: yeni veya güncellenmiþ kayýt yok.");

                return;
            }

            await SaveOrdersToPostgreSql(
                orders,
                stoppingToken);

            DateTime newestUpdate =
                orders.Max(x => x.UpdatedAt);

            await UpdateLastSyncTime(
                "Orders",
                newestUpdate,
                stoppingToken);

            _logger.LogInformation(
                "Orders: {Count} kayýt senkronize edildi.",
                orders.Count);
        }


        private async Task<List<Order>>
            GetOrdersFromSqlServer(
                DateTime lastSync,
                CancellationToken stoppingToken)
        {
            List<Order> orders = new();

            await using SqlConnection connection =
                new SqlConnection(
                    GetSqlServerConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                SELECT
                    OrderId,
                    CustomerId,
                    ProductId,
                    EmployeeId,
                    Quantity,
                    TotalAmount,
                    OrderDate,
                    UpdatedAt
                FROM dbo.Orders
                WHERE UpdatedAt > @LastSyncTime
                ORDER BY UpdatedAt;";

            await using SqlCommand command =
                new SqlCommand(query, connection);

            command.Parameters
                .Add("@LastSyncTime", SqlDbType.DateTime2)
                .Value = lastSync;

            await using SqlDataReader reader =
                await command.ExecuteReaderAsync(stoppingToken);

            while (await reader.ReadAsync(stoppingToken))
            {
                Order order = new Order
                {
                    OrderId = reader.GetInt32(0),
                    CustomerId = reader.GetInt32(1),
                    ProductId = reader.GetInt32(2),
                    EmployeeId = reader.GetInt32(3),
                    Quantity = reader.GetInt32(4),
                    TotalAmount = reader.GetDecimal(5),
                    OrderDate = reader.GetDateTime(6),
                    UpdatedAt = reader.GetDateTime(7)
                };

                orders.Add(order);
            }

            return orders;
        }


        private async Task SaveOrdersToPostgreSql(
            List<Order> orders,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            foreach (Order order in orders)
            {
                const string query = @"
                    INSERT INTO purchases
                    (
                        purchase_id,
                        client_id,
                        item_id,
                        staff_id,
                        quantity,
                        total_price,
                        purchase_date,
                        modified_at
                    )
                    VALUES
                    (
                        @purchase_id,
                        @client_id,
                        @item_id,
                        @staff_id,
                        @quantity,
                        @total_price,
                        @purchase_date,
                        @modified_at
                    )
                    ON CONFLICT (purchase_id)
                    DO UPDATE SET
                        client_id = EXCLUDED.client_id,
                        item_id = EXCLUDED.item_id,
                        staff_id = EXCLUDED.staff_id,
                        quantity = EXCLUDED.quantity,
                        total_price = EXCLUDED.total_price,
                        purchase_date = EXCLUDED.purchase_date,
                        modified_at = EXCLUDED.modified_at;";

                await using NpgsqlCommand command =
                    new NpgsqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "purchase_id",
                    order.OrderId);

                command.Parameters.AddWithValue(
                    "client_id",
                    order.CustomerId);

                command.Parameters.AddWithValue(
                    "item_id",
                    order.ProductId);

                command.Parameters.AddWithValue(
                    "staff_id",
                    order.EmployeeId);

                command.Parameters.AddWithValue(
                    "quantity",
                    order.Quantity);

                command.Parameters.AddWithValue(
                    "total_price",
                    order.TotalAmount);

                command.Parameters.AddWithValue(
                    "purchase_date",
                    order.OrderDate);

                command.Parameters.AddWithValue(
                    "modified_at",
                    order.UpdatedAt);

                await command.ExecuteNonQueryAsync(stoppingToken);
            }
        }


        // =========================================================
        // PAYMENTS -> TRANSACTIONS
        // =========================================================

        private async Task SyncPayments(
            CancellationToken stoppingToken)
        {
            DateTime lastSync =
                await GetLastSyncTime(
                    "Payments",
                    stoppingToken);

            List<Payment> payments =
                await GetPaymentsFromSqlServer(
                    lastSync,
                    stoppingToken);

            if (payments.Count == 0)
            {
                _logger.LogInformation(
                    "Payments: yeni veya güncellenmiþ kayýt yok.");

                return;
            }

            await SavePaymentsToPostgreSql(
                payments,
                stoppingToken);

            DateTime newestUpdate =
                payments.Max(x => x.UpdatedAt);

            await UpdateLastSyncTime(
                "Payments",
                newestUpdate,
                stoppingToken);

            _logger.LogInformation(
                "Payments: {Count} kayýt senkronize edildi.",
                payments.Count);
        }


        private async Task<List<Payment>>
            GetPaymentsFromSqlServer(
                DateTime lastSync,
                CancellationToken stoppingToken)
        {
            List<Payment> payments = new();

            await using SqlConnection connection =
                new SqlConnection(
                    GetSqlServerConnectionString());

            await connection.OpenAsync(stoppingToken);

            const string query = @"
                SELECT
                    PaymentId,
                    OrderId,
                    PaymentMethod,
                    Amount,
                    PaymentStatus,
                    PaymentDate,
                    UpdatedAt
                FROM dbo.Payments
                WHERE UpdatedAt > @LastSyncTime
                ORDER BY UpdatedAt;";

            await using SqlCommand command =
                new SqlCommand(query, connection);

            command.Parameters
                .Add("@LastSyncTime", SqlDbType.DateTime2)
                .Value = lastSync;

            await using SqlDataReader reader =
                await command.ExecuteReaderAsync(stoppingToken);

            while (await reader.ReadAsync(stoppingToken))
            {
                Payment payment = new Payment
                {
                    PaymentId = reader.GetInt32(0),
                    OrderId = reader.GetInt32(1),
                    PaymentMethod = reader.GetString(2),
                    Amount = reader.GetDecimal(3),
                    PaymentStatus = reader.GetString(4),
                    PaymentDate = reader.GetDateTime(5),
                    UpdatedAt = reader.GetDateTime(6)
                };

                payments.Add(payment);
            }

            return payments;
        }


        private async Task SavePaymentsToPostgreSql(
            List<Payment> payments,
            CancellationToken stoppingToken)
        {
            await using NpgsqlConnection connection =
                new NpgsqlConnection(
                    GetPostgreSqlConnectionString());

            await connection.OpenAsync(stoppingToken);

            foreach (Payment payment in payments)
            {
                const string query = @"
                    INSERT INTO transactions
                    (
                        transaction_id,
                        purchase_id,
                        payment_type,
                        paid_amount,
                        transaction_status,
                        transaction_date,
                        modified_at
                    )
                    VALUES
                    (
                        @transaction_id,
                        @purchase_id,
                        @payment_type,
                        @paid_amount,
                        @transaction_status,
                        @transaction_date,
                        @modified_at
                    )
                    ON CONFLICT (transaction_id)
                    DO UPDATE SET
                        purchase_id = EXCLUDED.purchase_id,
                        payment_type = EXCLUDED.payment_type,
                        paid_amount = EXCLUDED.paid_amount,
                        transaction_status = EXCLUDED.transaction_status,
                        transaction_date = EXCLUDED.transaction_date,
                        modified_at = EXCLUDED.modified_at;";

                await using NpgsqlCommand command =
                    new NpgsqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "transaction_id",
                    payment.PaymentId);

                command.Parameters.AddWithValue(
                    "purchase_id",
                    payment.OrderId);

                command.Parameters.AddWithValue(
                    "payment_type",
                    payment.PaymentMethod);

                command.Parameters.AddWithValue(
                    "paid_amount",
                    payment.Amount);

                command.Parameters.AddWithValue(
                    "transaction_status",
                    payment.PaymentStatus);

                command.Parameters.AddWithValue(
                    "transaction_date",
                    payment.PaymentDate);

                command.Parameters.AddWithValue(
                    "modified_at",
                    payment.UpdatedAt);

                await command.ExecuteNonQueryAsync(stoppingToken);
            }
        }
    }
}