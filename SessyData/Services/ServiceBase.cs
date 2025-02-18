using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

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

        public void Store(List<T> list, Func<T, ModelContext, bool> func)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var item in list)
                {
                    if(!func(item, db))
                    {
                        db.Set<T>().Add(item);
                    }
                }
            });
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
