namespace Factory.Api.Tests;

public sealed class SqlSelectValidatorTests
{
    [Theory]
    [InlineData("SELECT * FROM factory.task")]
    [InlineData("select id from github.repository")]
    [InlineData("  WITH recent AS (SELECT 1) SELECT * FROM recent")]
    [InlineData("SELECT * FROM factory.task t JOIN github.repository r ON r.id = t.repository_id;")]
    [InlineData("SELECT 'delete this later' AS note")]
    [InlineData("SELECT 1 -- drop table factory.task")]
    [InlineData("SELECT 1 /* update whatever */")]
    public void Accepts_a_single_select_or_with_select_statement(string sql)
    {
        Assert.True(SqlSelectValidator.IsReadOnlySelect(sql, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_empty_input(string? sql)
    {
        Assert.False(SqlSelectValidator.IsReadOnlySelect(sql, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("INSERT INTO factory.task DEFAULT VALUES")]
    [InlineData("UPDATE factory.task SET status='Cancelled'")]
    [InlineData("DELETE FROM factory.task")]
    [InlineData("DROP TABLE factory.task")]
    [InlineData("ALTER TABLE factory.task ADD COLUMN x int")]
    [InlineData("TRUNCATE factory.task")]
    [InlineData("GRANT ALL ON factory.task TO public")]
    [InlineData("CREATE TABLE evil (id int)")]
    [InlineData("CALL some_procedure()")]
    public void Rejects_write_statements_outright(string sql)
    {
        Assert.False(SqlSelectValidator.IsReadOnlySelect(sql, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_a_second_statement_smuggled_after_a_semicolon()
    {
        Assert.False(SqlSelectValidator.IsReadOnlySelect("SELECT 1; DROP TABLE factory.task;", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_a_write_keyword_hidden_behind_a_select_prefix()
    {
        Assert.False(SqlSelectValidator.IsReadOnlySelect("SELECT 1; DELETE FROM factory.task", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_statements_that_are_not_select_or_with()
    {
        Assert.False(SqlSelectValidator.IsReadOnlySelect("EXPLAIN SELECT 1", out var error));
        Assert.NotNull(error);
    }
}
