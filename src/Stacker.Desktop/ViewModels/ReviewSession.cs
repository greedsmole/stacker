// Purpose: Small persisted-session identity used to separate review drafts by repository/account/PR context.
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Desktop.ViewModels;

// A repository/account/PR-scoped session shared by the workspace, inline editors and inspector.
public sealed class ReviewSession(IGitHubReader reader, IGitHubWriter writer, ApplicationStore store,
    IGitRepositoryReader git, GitHubRepositoryContext context, PullRequest pr, DiffSectionViewModel section, bool demo)
    : ReviewViewModel(reader, writer, store, git, context, pr, section, demo);
