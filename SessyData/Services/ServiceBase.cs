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

        public async Task AddRange(List<T> list)
        {
            await _dbHelper.ExecuteTransaction(async db =>
            {
                db.Set<T>().AddRange(list);

                await Task.FromResult<bool>(true);
            });
        }

        public async Task Add(List<T> list, Func<T, DbSet<T>, bool>? contains = null)
        {
            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    if (contains == null || !contains(item, db.Set<T>()))
                    {
                        db.Set<T>().Add(item);
                    }

                    await Task.FromResult<bool>(true);
                }
            });
        }

        private static void EnsureUpdatable()
        {
            if (!typeof(IUpdatable<T>).IsAssignableFrom(typeof(T)))
                throw new InvalidCastException($"For StoreOrUpdate the type {typeof(T).Name} must implement IUpdatable<{typeof(T).Name}>");
        }

        public async Task Add(List<T> list, Func<T, DbSet<T>, T?> contains, bool checkDuplicate = true)
        {
            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item);

                    if (containedItem != null)
                    {
                        if (checkDuplicate)
                            throw new InvalidOperationException($"Item to add is duplicate {item}");
                    }
                    else
                    {
                        db.Attach(item);

                        db.Set<T>().Add(item);
                    }
                }

                await Task.FromResult<bool>(true);
            });
        }

        /// <summary>
        /// Adds or updates an item in the set 'T'.
        /// 'T' Must be of type IUpdatable<T> and you must provide the Update() code in class 'T'.
        /// </summary>
        /// <param name="list">List of IUpdatable<T> objects to Add or Update.</param>
        /// <param name="contains">
        /// If 'contains' is not null it will be called to fetch the unique item from the set. You must
        /// provide the code to fetch the item.
        /// if 'contains' is null the routine fetches the item for you using it's 'Id'.
        /// </param>
        public async Task AddOrUpdate(List<T> list, Func<T, DbSet<T>, T?>? contains = null)
        {
            EnsureUpdatable();

            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (T item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item, false);

                    if (containedItem != null)
                    {
                        ((IUpdatable<T>)containedItem!).Update(item);

                        var state = db.Entry(containedItem).State;

                        var entry = db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        db.Set<T>().Add(item);
                    }
                }

                await Task.FromResult<bool>(true);
            });
        }

        public async Task Update(List<T> list, Func<T, DbSet<T>, T?> contains)
        {
            EnsureUpdatable();

            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item);

                    if (containedItem != null)
                    {
                        ((IUpdatable<T>)containedItem!).Update(item);

                        var state = db.Entry(containedItem).State;

                        db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Item to update was not found {item}");
                    }
                }

                await Task.FromResult<bool>(true);
            });
        }

        public async Task Remove(List<T> list, Func<T, DbSet<T>, T?> contains)
        {
            EnsureUpdatable();

            await _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = await GetContainedItem(contains, db, item);

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

        private async Task<T?> GetContainedItem(Func<T, DbSet<T>, T?>? contains, ModelContext db, T item, bool shouldExist = true)
        {
            T? containedItem;

            if (contains != null)
            {
                containedItem = contains(item, db.Set<T>());
            }
            else
            {
                containedItem = await db.Set<T>().FindAsync(((IUpdatable<T>)item).Id);
            }

            if (containedItem == null && shouldExist)
                throw new InvalidOperationException($"Not found {((IUpdatable<T>)item).Id}");

            return containedItem;
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

        public async Task<bool> Exists(Func<DbSet<T>, Task<bool>> func)
        {
            return await _dbHelper.ExecuteQuery(async (ModelContext db) =>
            {
                return await func(db.Set<T>());
            });
        }

        public async Task<T?> Get(Func<IQueryable<T>, Task<T?>> func)
        {
            return await _dbHelper.ExecuteQuery(async (ModelContext db) =>
            {
                return await func(db.Set<T>().AsNoTracking());
            });
        }

        public async Task<List<T>> GetList(Func<IQueryable<T>, Task<List<T>>> func)
        {
            return await _dbHelper.ExecuteQuery(async (ModelContext db) =>
            {
                return await func(db.Set<T>().AsNoTracking());
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
