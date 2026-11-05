using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Trigram (pg_trgm GIN) indexes for the "contains" searches, which were full table scans: chat
    /// (ILIKE '%q%' on ChatMessages.Content) and mail (LIKE '%q%' on lower(Subject) / lower(Body)). Raw SQL because the
    /// mail ones are expression indexes. Skipped with a warning where pg_trgm is unavailable (search still works).
    /// </summary>
    public partial class SearchTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS pg_trgm;
                    CREATE INDEX IF NOT EXISTS "IX_ChatMessages_Content_trgm" ON "ChatMessages" USING gin ("Content" gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS "IX_Mails_Subject_lower_trgm" ON "Mails" USING gin (lower("Subject") gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS "IX_Mails_Body_lower_trgm" ON "Mails" USING gin (lower("Body") gin_trgm_ops);
                EXCEPTION WHEN OTHERS THEN
                    RAISE WARNING 'Search trigram indexes not added: %', SQLERRM;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_ChatMessages_Content_trgm";
                DROP INDEX IF EXISTS "IX_Mails_Subject_lower_trgm";
                DROP INDEX IF EXISTS "IX_Mails_Body_lower_trgm";
                """);
        }
    }
}
