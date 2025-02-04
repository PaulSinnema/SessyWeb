using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Extensions;
using SessyData.Model;

namespace SessyData.Helpers
{

    public class DbHelper
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public DbHelper(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public void ExecuteTransaction(Action<ModelContext> action)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();
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
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();
                return queryFunc(dbContext);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database query error: {ex.Message}", ex);
            }
        }
    }
}
