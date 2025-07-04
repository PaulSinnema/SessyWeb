﻿using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class TaxesDataService : ServiceBase<Taxes>
    {
        public TaxesDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        /// <summary>
        /// Get the active Tax record for a date. Returns null if no record is found.
        /// </summary>
        public Taxes? GetTaxesForDate(DateTime time)
        {
            return Get((set) =>
            {
                var result = set
                    .Where(tx => tx.Time <= time)
                    .OrderByDescending(tx => tx.Time)
                    .FirstOrDefault();

                return result;
            });
        }
    }
}
