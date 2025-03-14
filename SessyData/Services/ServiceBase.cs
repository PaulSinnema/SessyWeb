using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace SessyData.Services
{
    public class ServiceBase<T> : IDisposable
        where T : class, new()
    {
        private IServiceScope _scope { get; set; }
        protected DbHelper _dbHelper { get; set; }

        public ServiceBase(IServiceScopeFactory serviceScopeFactory)
        {
             _scope = serviceScopeFactory.CreateScope();
            _dbHelper = _scope.ServiceProvider.GetRequiredService<DbHelper>();
        }

        public void Store(List<T> list)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                db.Set<T>().AddRange(list);
            });
        }

        public void Store(List<T> list, Func<T, ModelContext, bool> contains)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var item in list)
                {
                    if(!contains(item, db))
                    {
                        db.Set<T>().Add(item);
                    }
                }
            });
        }

        private static void EnsureUpdatable()
        {
            if (!typeof(IUpdatable<T>).IsAssignableFrom(typeof(T)))
                throw new InvalidCastException($"For StoreOrUpdate the type {typeof(T).Name} must implement IUpdatable<{typeof(T).Name}>");
        }

        public void Add(List<T> list, Func<T, ModelContext, T?> contains)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db);

                    if (containedItem != null)
                    {
                        throw new InvalidOperationException($"Item to add is duplicate {item}");
                    }
                    else
                    {
                        db.Set<T>().Add(item);
                    }
                }
            });
        }

        public void AddOrUpdate(List<T> list, Func<T, ModelContext, T?> contains)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db);

                    if (containedItem != null)
                    {
                        var itemToUpdate = await GetByKeyAsync(db, ((IUpdatable<T>)containedItem).Id);

                        ((IUpdatable<T>)itemToUpdate!).Update(itemToUpdate);

                        db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        db.Set<T>().Add(item);
                    }
                }
            });
        }

        public void Update(List<T> list, Func<T, ModelContext, T?> contains)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db);

                    if (containedItem != null)
                    {
                        var itemToUpdate = await GetByKeyAsync(db, ((IUpdatable<T>)containedItem).Id);

                        ((IUpdatable<T>)itemToUpdate!).Update(itemToUpdate);

                        db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Item to update was not fout {item}");
                    }
                }
            });
        }

        public void Remove(List<T> list, Func<T, ModelContext, T?> contains)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db);

                    if (containedItem != null)
                    {
                        var itemToRemove = await GetByKeyAsync(db, ((IUpdatable<T>)containedItem).Id);

                        db.Set<T>().Remove(containedItem);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Item to remove was not found {item}");
                    }
                }
            });
        }

        private async Task<T?> GetByKeyAsync(ModelContext db, int key)
        {
            var entityType = typeof(T);
            var keyProperty = entityType.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() != null)
                ?? entityType.GetProperties().FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));

            if (keyProperty == null)
                throw new InvalidOperationException($"No [Key] attribute found on {entityType.Name}, and no default 'Id' property detected.");

            return await db.Set<T>().FindAsync(key);
        }

        public T? Get(Func<ModelContext, T?> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext dbContext) =>
            {
                return func(dbContext);
            });
        }

        public List<T> GetList(Func<ModelContext, List<T>> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext dbContext) =>
            {
                return func(dbContext);
            });
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _scope.Dispose();
                _isDisposed = true;
            }
        }
    }
}
