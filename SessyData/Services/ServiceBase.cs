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

        public void AddRange(List<T> list)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                db.Set<T>().AddRange(list);
            });
        }

        public void Add(List<T> list, Func<T, DbSet<T>, bool>? contains = null)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var item in list)
                {
                    if(contains == null || !contains(item, db.Set<T>()))
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

        public void Add(List<T> list, Func<T, DbSet<T>, T?> contains, bool checkDuplicate = true)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db.Set<T>());

                    if (containedItem != null)
                    {
                        if(checkDuplicate)
                            throw new InvalidOperationException($"Item to add is duplicate {item}");
                    }
                    else
                    {
                        db.Set<T>().Add(item);
                    }
                }
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
        public void AddOrUpdate(List<T> list, Func<T, DbSet<T>, T?>? contains = null)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (T item in list)
                {
                    T? containedItem = null;

                    if (contains != null)
                    {
                        containedItem = contains(item, db.Set<T>());
                    }
                    else
                    {
                        containedItem = db.Set<T>()
                                            .Where(setItem => ((IUpdatable<T>)setItem).Id == ((IUpdatable<T>)item).Id)
                                            .SingleOrDefault();
                    }

                    if (containedItem != null)
                    {
                        var itemToUpdate = await GetByKeyAsync(db, ((IUpdatable<T>)containedItem).Id);

                        ((IUpdatable<T>)itemToUpdate!).Update(item);

                        db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        db.Set<T>().Add(item);
                    }
                }
            });
        }

        public void Update(List<T> list, Func<T, DbSet<T>, T?> contains)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db.Set<T>());

                    if (containedItem != null)
                    {
                        var itemToUpdate = await GetByKeyAsync(db, ((IUpdatable<T>)containedItem).Id);

                        ((IUpdatable<T>)itemToUpdate!).Update(item);

                        db.Set<T>().Update(containedItem);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Item to update was not found {item}");
                    }
                }
            });
        }

        public void Remove(List<T> list, Func<T, DbSet<T>, T?> contains)
        {
            EnsureUpdatable();

            _dbHelper.ExecuteTransaction(async db =>
            {
                foreach (var item in list)
                {
                    var containedItem = contains(item, db.Set<T>());

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

        public T? Get(Func<DbSet<T>, T?> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext db) =>
            {
                return func(db.Set<T>());
            });
        }

        public List<T> GetList(Func<DbSet<T>, List<T>> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext db) =>
            {
                return func(db.Set<T>());
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
