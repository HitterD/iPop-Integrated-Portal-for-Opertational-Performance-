using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotesSearchTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Notes search is `LIKE '%q%'` on lower(Title) / lower(PlainText): trigram indexes turn the full scan into an index
            // lookup. Skipped with a warning where pg_trgm is unavailable (search still works, just slower).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS pg_trgm;
                    CREATE INDEX IF NOT EXISTS "IX_NotePages_Title_lower_trgm" ON "NotePages" USING gin (lower("Title") gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS "IX_NotePages_PlainText_lower_trgm" ON "NotePages" USING gin (lower("PlainText") gin_trgm_ops);
                EXCEPTION WHEN OTHERS THEN
                    RAISE WARNING 'Notes trigram indexes not added: %', SQLERRM;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_NotePages_Title_lower_trgm";
                DROP INDEX IF EXISTS "IX_NotePages_PlainText_lower_trgm";
                """);
        }
    }
}
