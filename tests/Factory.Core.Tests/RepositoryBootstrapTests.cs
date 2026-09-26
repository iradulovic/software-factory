using Factory.Core;

namespace Factory.Core.Tests;

public sealed class RepositoryBootstrapTests
{
    [Fact]
    public void Product_brief_renders_a_stable_readable_handoff_issue()
    {
        var brief = new ProductBrief(
            "Field Notes",
            "A signed-in researcher creates a note and sees it in the dashboard.",
            "ASP.NET Core API",
            "Auth0",
            "Azure Container Apps",
            ApplicationShell.Both);

        var body = BootstrapIssueBody.Render(brief);

        var expected = """
            Read AGENTS.md and NEW_APP.md. Derive this application from the recorded base release. Use the both shell. Implement the first journey described in the product brief. Keep authentication and authorization separate and connect the selected backend through its OpenAPI contract. Begin in local mock mode if backend/provider details are not yet available; report what remains necessary for production.

            ## Product brief

            - **Product:** Field Notes
            - **Shell:** both
            - **Backend:** ASP.NET Core API
            - **Authentication provider:** Auth0
            - **Deploy target:** Azure Container Apps

            ## First journey

            A signed-in researcher creates a note and sees it in the dashboard.
            """.ReplaceLineEndings("\n");

        Assert.Equal(expected, body);
    }
}
