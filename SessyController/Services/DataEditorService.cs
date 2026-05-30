using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;
using System.Reflection;

namespace SessyController.Services
{
    /// <summary>
    /// Generic data editor service. Loads and saves any IUpdatable entity via EF Core.
    /// </summary>
    public class DataEditorService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public DataEditorService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        /// <summary>
        /// Returns all DbSet table names from ModelContext that contain IUpdatable entities.
        /// </summary>
        public List<TableInfo> GetEditableTables()
        {
            return typeof(ModelContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(p => new TableInfo
                {
                    PropertyName = p.Name,
                    EntityType = p.PropertyType.GetGenericArguments()[0]
                })
                .Where(t => typeof(IUpdatable<>)
                    .MakeGenericType(t.EntityType)
                    .IsAssignableFrom(t.EntityType))
                .OrderBy(t => t.PropertyName)
                .ToList();
        }

        /// <summary>
        /// Loads entities filtered by optional WHERE clause, paged.
        /// </summary>
        public async Task<EditorResult> LoadAsync(TableInfo table, string? whereClause, int page, int pageSize)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModelContext>();
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            try
            {
                var tableName = GetTableName(db, table.EntityType);
                var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
                var offset = (page - 1) * pageSize;

                using var countCmd = conn.CreateCommand();
                countCmd.CommandText = $"SELECT COUNT(*) FROM {tableName}{where}";
                var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM {tableName}{where} LIMIT {pageSize} OFFSET {offset}";
                using var reader = await cmd.ExecuteReaderAsync();

                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.GetName(i))
                    .ToList();

                var rows = new List<Dictionary<string, object?>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }

                return new EditorResult { Columns = columns, Rows = rows, TotalCount = total };
            }
            finally
            {
                await conn.CloseAsync();
            }
        }

        /// <summary>
        /// Saves a single edited row via EF Core IUpdatable.Update().
        /// </summary>
        public async Task<string?> SaveRowAsync(TableInfo table, Dictionary<string, object?> row)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ModelContext>();

                if (!row.TryGetValue("Id", out var idObj) || idObj == null)
                    return "Row has no Id.";

                var id = Convert.ToInt32(idObj);

                // Load entity by Id via EF Core.
                var entity = await db.FindAsync(table.EntityType, id);

                if (entity == null) return $"Row with Id={id} not found.";

                // Create updated instance and call IUpdatable.Update().
                var updated = CreateEntityFromRow(table.EntityType, row);
                table.EntityType.GetMethod("Update")!.Invoke(entity, new[] { updated });

                await db.SaveChangesAsync();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string GetTableName(DbContext db, Type entityType)
            => db.Model.FindEntityType(entityType)?.GetTableName() ?? entityType.Name;

        private static object CreateEntityFromRow(Type type, Dictionary<string, object?> row)
        {
            var entity = Activator.CreateInstance(type)!;
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;
                if (!row.TryGetValue(prop.Name, out var val) || val == null) continue;
                try
                {
                    var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    var converted = target.IsEnum
                        ? Enum.ToObject(target, Convert.ToInt32(val))
                        : Convert.ChangeType(val, target);
                    prop.SetValue(entity, converted);
                }
                catch { /* skip unconvertible */ }
            }
            return entity;
        }
    }

    public class TableInfo
    {
        public string PropertyName { get; set; } = "";
        public Type EntityType { get; set; } = typeof(object);
        public string DisplayName => PropertyName;
    }

    public class EditorResult
    {
        public List<string> Columns { get; set; } = [];
        public List<Dictionary<string, object?>> Rows { get; set; } = [];
        public int TotalCount { get; set; }
    }
}