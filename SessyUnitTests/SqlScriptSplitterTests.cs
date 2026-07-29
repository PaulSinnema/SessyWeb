using SessyController.Services;

namespace SessyUnitTests
{
    /// <summary>
    /// Covers the splitter behind the SQL Console. The cases mirror what the repair scripts
    /// in Sql/ actually contain: dot-commands, BEGIN/COMMIT, comments holding semicolons,
    /// and temp tables that must survive from one statement to the next.
    /// </summary>
    public class SqlScriptSplitterTests
    {
        [Fact]
        public void Splits_On_Semicolons()
        {
            var statements = SqlScriptSplitter.Split("SELECT 1; SELECT 2; SELECT 3;", out var skipped);

            Assert.Equal(3, statements.Count);
            Assert.Equal("SELECT 1", statements[0]);
            Assert.Equal("SELECT 3", statements[2]);
            Assert.Empty(skipped);
        }

        [Fact]
        public void Keeps_Trailing_Statement_Without_Semicolon()
        {
            var statements = SqlScriptSplitter.Split("SELECT 1; SELECT 2", out _);

            Assert.Equal(2, statements.Count);
            Assert.Equal("SELECT 2", statements[1]);
        }

        [Fact]
        public void Ignores_Semicolon_Inside_String_Literal()
        {
            var statements = SqlScriptSplitter.Split("SELECT 'a;b' AS x; SELECT 2;", out _);

            Assert.Equal(2, statements.Count);
            Assert.Equal("SELECT 'a;b' AS x", statements[0]);
        }

        [Fact]
        public void Handles_Escaped_Quote_Inside_String_Literal()
        {
            var statements = SqlScriptSplitter.Split("SELECT 'it''s; here' AS x; SELECT 2;", out _);

            Assert.Equal(2, statements.Count);
            Assert.Equal("SELECT 'it''s; here' AS x", statements[0]);
        }

        [Fact]
        public void Ignores_Semicolon_Inside_Quoted_Identifier()
        {
            var statements = SqlScriptSplitter.Split("SELECT \"odd;name\" FROM t; SELECT 2;", out _);

            Assert.Equal(2, statements.Count);
            Assert.Equal("SELECT \"odd;name\" FROM t", statements[0]);
        }

        [Fact]
        public void Strips_Line_Comments_Including_Their_Semicolons()
        {
            var statements = SqlScriptSplitter.Split("-- drop everything; really\nSELECT 1;", out _);

            Assert.Single(statements);
            Assert.Equal("SELECT 1", statements[0]);
        }

        [Fact]
        public void Strips_Block_Comments_And_Keeps_Tokens_Apart()
        {
            var statements = SqlScriptSplitter.Split("SELECT/*a;b*/1;", out _);

            Assert.Single(statements);
            Assert.Equal("SELECT 1", statements[0]);
        }

        [Fact]
        public void Returns_Nothing_For_A_Comment_Only_Script()
        {
            var statements = SqlScriptSplitter.Split("-- just a note\n/* and another */", out var skipped);

            Assert.Empty(statements);
            Assert.Empty(skipped);
        }

        [Fact]
        public void Skips_Dot_Commands_And_Reports_Them()
        {
            var statements = SqlScriptSplitter.Split(".headers on\n.mode column\nSELECT 1;", out var skipped);

            Assert.Single(statements);
            Assert.Equal("SELECT 1", statements[0]);
            Assert.Equal(new[] { ".headers on", ".mode column" }, skipped);
        }

        [Fact]
        public void Keeps_Dots_That_Are_Not_Shell_Commands()
        {
            var statements = SqlScriptSplitter.Split(
                "DROP TABLE IF EXISTS temp.bounds; SELECT 1.5 AS x;", out var skipped);

            Assert.Equal(2, statements.Count);
            Assert.Equal("DROP TABLE IF EXISTS temp.bounds", statements[0]);
            Assert.Equal("SELECT 1.5 AS x", statements[1]);
            Assert.Empty(skipped);
        }

        [Fact]
        public void Skips_Transaction_Control()
        {
            var statements = SqlScriptSplitter.Split(
                "BEGIN TRANSACTION;\nSELECT 1;\nCOMMIT;", out var skipped);

            Assert.Single(statements);
            Assert.Equal("SELECT 1", statements[0]);
            Assert.Equal(new[] { "BEGIN TRANSACTION", "COMMIT" }, skipped);
        }

        [Fact]
        public void Does_Not_Mistake_An_Identifier_For_Transaction_Control()
        {
            var statements = SqlScriptSplitter.Split("BEGINNING_BALANCE_UPDATE_PLACEHOLDER", out var skipped);

            Assert.Single(statements);
            Assert.Empty(skipped);
        }

        [Fact]
        public void Rejects_Create_Trigger()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SqlScriptSplitter.Split("CREATE TRIGGER t AFTER INSERT ON x BEGIN SELECT 1; END;", out _));

            Assert.Contains("CREATE TRIGGER", ex.Message);
        }

        [Fact]
        public void Splits_The_Repair_Script_Shape()
        {
            // Condensed version of Sql/RepairEnergyHistoryGap.sql: dot-commands, an explicit
            // transaction, temp tables and a CTE-driven insert.
            const string script = @"
.headers on
.mode column

BEGIN TRANSACTION;

DROP TABLE IF EXISTS temp.p;
CREATE TEMP TABLE p AS SELECT '2026-07-29 10:30:00' AS gap_end, 14 AS profile_days;

SELECT '=== ANCHORS ===' AS check_1;

INSERT INTO EnergyHistory (Time, MeterId)
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
SELECT '2026-07-28 00:00:00', 'P1' FROM n WHERE i = 1;

COMMIT;
";

            var statements = SqlScriptSplitter.Split(script, out var skipped);

            Assert.Equal(4, statements.Count);
            Assert.StartsWith("DROP TABLE IF EXISTS temp.p", statements[0]);
            Assert.StartsWith("CREATE TEMP TABLE p", statements[1]);
            Assert.StartsWith("SELECT '=== ANCHORS ==='", statements[2]);
            Assert.StartsWith("INSERT INTO EnergyHistory", statements[3]);

            Assert.Equal(new[] { ".headers on", ".mode column", "BEGIN TRANSACTION", "COMMIT" }, skipped);
        }
    }
}
