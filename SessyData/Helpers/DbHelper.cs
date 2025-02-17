using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Extensions;
using SessyData.Model;

namespace SessyData.Helpers
{

    public class DbHelper : IDisposable
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private IServiceScope _scope { get; set; }

        public DbHelper(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _scope = _serviceScopeFactory.CreateScope();
        }

        public void ExecuteTransaction(Action<ModelContext> action)
        {
            try
            {
                var dbContext = _scope.ServiceProvider.GetRequiredService<ModelContext>();
                using var transaction = dbContext.Database.BeginTransaction();

                try
                {
                    action(dbContext);
                    dbContext.SaveChanges();
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new InvalidOperationException($"Database transaction failed: {ex.ToDetailedString()}", ex);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database operation error: {ex.ToDetailedString()}", ex);
            }
        }

        public T ExecuteQuery<T>(Func<ModelContext, T> queryFunc)
        {
            try
            {
                var dbContext = _scope.ServiceProvider.GetRequiredService<ModelContext>();

                return queryFunc(dbContext);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database query error: {ex.Message}", ex);
            }
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if(!_isDisposed)
            {
                _scope.Dispose();
            }
        }
    }
}
