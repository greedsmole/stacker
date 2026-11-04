using Stacker.Core;
namespace Stacker.Infrastructure;

internal static class GitEnvironment
{
    internal static readonly string[] RepositoryRedirects=["GIT_DIR","GIT_WORK_TREE","GIT_COMMON_DIR","GIT_INDEX_FILE","GIT_OBJECT_DIRECTORY","GIT_ALTERNATE_OBJECT_DIRECTORIES"];
    internal static ProcessRequest ForRepository(ProcessRequest request)=>request with
    {UnsetEnvironment=(request.UnsetEnvironment??[]).Concat(RepositoryRedirects).Distinct().ToArray()};
}
