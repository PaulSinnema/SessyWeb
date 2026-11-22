using Radzen;
using Radzen.Blazor;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Helpers
{
    /// <summary>Server-side paging for Radzen data grids: the grid's filter, sort and page in one place.</summary>
    public static class GridQuery
    {
        /// <summary>
        /// The requested page and the total count after filtering. When a filter leaves fewer rows than
        /// the current page starts at, the grid goes back to its first page.
        /// </summary>
        public static (List<T> Page, int Count) ToGridPage<T>(this IQueryable<T> query, LoadDataArgs args, RadzenDataGrid<T> grid) where T : notnull
        {
            if (!string.IsNullOrEmpty(args.Filter))
                query = query.Where(grid.ColumnsCollection);

            if (!string.IsNullOrEmpty(args.OrderBy))
                query = query.OrderBy(args.OrderBy);

            int count = query.Count();

            if (args.Skip > count)
            {
                args.Skip = 0;
                grid.CurrentPage = 0;
            }

            var page = args.Skip.HasValue && args.Top.HasValue
                ? query.Skip(args.Skip.Value).Take(args.Top.Value).ToList()
                : query.ToList();

            return (page, count);
        }
    }
}
