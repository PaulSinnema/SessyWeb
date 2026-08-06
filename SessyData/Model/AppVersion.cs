using Microsoft.EntityFrameworkCore;
using SessyCommon.Attributes;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Which application versions have run against this database. One row per version, written at
    /// startup right after the migrations are applied.
    ///
    /// Answers two questions a bare database file cannot: which build wrote this data, and whether
    /// an older build has been started on a database a newer one already migrated.
    /// </summary>
    [Index(nameof(Version), IsUnique = true)]
    public class AppVersion : IUpdatable<AppVersion>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Application version, e.g. "v1.0.39" — SessyCommon.AppInfo.Version.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>First startup of this version against this database. Never overwritten.</summary>
        [SkipCopy]
        public DateTime FirstSeen { get; set; }

        /// <summary>Most recent startup of this version against this database.</summary>
        public DateTime LastSeen { get; set; }

        /// <summary>Last EF migration applied when this version last started.</summary>
        public string LastMigration { get; set; } = string.Empty;

        public void Update(AppVersion updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}
