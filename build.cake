//////////////////////////////////////////////////////////////////////
// TOOLS
//////////////////////////////////////////////////////////////////////
#tool "nuget:?package=GitVersion.CommandLine&version=4.0.0"
#addin "nuget:?package=Cake.Docker&version=0.10.0"
#addin "nuget:?package=Cake.Incubator&version=5.1.0"

using Cake.Incubator.LoggingExtensions;

class OctopusDockerTag
{
    public string imageDirectory { get; set; }
    public string dockerNamespace { get; }
    public string imageName { get; }
    public string version { get; set; }
    public string tag { get; set; }
    private GitVersion gitVersion;

    public OctopusDockerTag(GitVersion version, string dockerNamespace, string imageDirectory) {

        this.dockerNamespace = dockerNamespace;
        this.gitVersion = version;
        this.imageDirectory = imageDirectory;
        this.version =  $"{gitVersion.Major}.{gitVersion.Minor}.{gitVersion.Patch}";
        this.imageName = this.CreateTag(this.version);
    }

    public string[] Tags() {
        return new string[] {
            this.CreateTag("latest"),
            this.CreateTag(this.version),
            this.CreateTag(this.gitVersion.Major.ToString()),
            this.CreateTag($"{this.gitVersion.Major}.{this.gitVersion.Minor}")
        };
    }

    private string CreateTag(string version) {
        return (version == "latest") ?
            $"{this.dockerNamespace}:{this.imageDirectory}" :
            $"{this.dockerNamespace}:{string.Join(".", version)}-{this.imageDirectory}";
    }
}

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

// This is the verbosity of the Cake logs - by defaulting to Normal we get better logging out of the gate
Context.Log.Verbosity = Argument("Verbosity", Verbosity.Normal);

var target = Argument("target", "Default");
var dockerNamespace = Argument("docker-namespace", "octopusdeploy/worker-tools");
var imageDirectory = Argument("image-directory", "ubuntu.18.04");

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

GitVersion gitVersionInfo;
OctopusDockerTag dockerTag;
string testContainerName = "test-container";

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
    var fromEnv = context.EnvironmentVariable("GitVersion.semVer");

    if (string.IsNullOrEmpty(fromEnv))
    {
        var tempPath = context.MakeAbsolute(context.Directory("./build")).Combine("temp");
        var logFilePath = tempPath.CombineWithFilePath("gitversion.log");

        try
        {
            if (IsRunningOnUnix())
            {
                using(var process = StartAndReturnProcess("xmlstarlet", new ProcessSettings{ Arguments = "edit -O --inplace --update \"//dllmap[@os='linux']/@target\" --value \"/lib64/libgit2.so.26\" tools/GitVersion.CommandLine.4.0.0/tools/LibGit2Sharp.dll.config" }))
                {
                    process.WaitForExit();
                    // This should output 0 as valid arguments supplied
                    Information("Exit code: {0}", process.GetExitCode());
                }
            }

            gitVersionInfo = GitVersion(new GitVersionSettings {
                NoFetch = true,
                OutputType = GitVersionOutput.Json,
                LogFilePath = logFilePath
            });
            Information("Informational Version {0}", gitVersionInfo.InformationalVersion);
            Verbose("GitVersion:\n{0}", gitVersionInfo.Dump());
            Information("Building {1} images v{0}", gitVersionInfo.SemVer, dockerNamespace);
            dockerTag = new OctopusDockerTag(gitVersionInfo, dockerNamespace, imageDirectory);
        }
        catch (Exception e)
        {
            Information(e);
        }
        finally
        {
            Information($"The GitVersion log file is available at {logFilePath}");

            if (BuildSystem.IsRunningOnTeamCity)
            {
                Information($"The GitVersion log file will be stored as a TeamCity build artifact called {logFilePath.GetFilename()} just in case you need to analyse it.");
                var buildSystem = BuildSystemAliases.BuildSystem(Context);
                buildSystem.TeamCity.PublishArtifacts(logFilePath.FullPath);
            }
        }
    }
    else
    {
        Information("Building {1} images v{0}", gitVersionInfo.SemVer, dockerNamespace);
    }

    if (BuildSystem.IsRunningOnTeamCity)
        BuildSystem.TeamCity.SetBuildNumber(gitVersionInfo.SemVer);
});

Teardown(context =>
{
    Information("Finished running tasks for build v{0}", gitVersionInfo.SemVer);
});

//////////////////////////////////////////////////////////////////////
//  PRIVATE TASKS
//////////////////////////////////////////////////////////////////////

Task("Build")
    .Does(() =>
{
    Information("Tags to be built:");
    dockerTag.Tags().ToList().ForEach((tag) => Information(tag));
    DockerBuild(new DockerImageBuildSettings { Tag = dockerTag.Tags() }, dockerTag.imageDirectory);

    Information("Building test container {1} with ContainerUnderTest={0}", dockerTag.imageName, testContainerName);

    var buildSettings = new DockerImageBuildSettings {
        Tag = new string[] { testContainerName },
        BuildArg = new string[] {  $"ContainerUnderTest={dockerTag.imageName}" }
    };

    if (IsRunningOnUnix()) {
        buildSettings.File = $"{dockerTag.imageDirectory}/Tests.Dockerfile";
    } else {
        buildSettings.File = $"{dockerTag.imageDirectory}\\Tests.Dockerfile";
    }

    DockerBuild(buildSettings, dockerTag.imageDirectory);
});

Task("Test")
    .IsDependentOn("Build")
    .Does(() =>
{
    var currentDirectory = MakeAbsolute(Directory("./"));
    try
    {
        Information("Running tests in {1} for {0}", dockerTag.imageName, testContainerName);

        ProcessSettings processSettings;

        if (IsRunningOnUnix()) {
            processSettings = new ProcessSettings{
                Arguments = $"run -v {currentDirectory}:/app {testContainerName} bash -c \"cd ./app/{dockerTag.imageDirectory} && ./scripts/run_tests_during_build.sh\""
            };
        } else {
            var specPath = "spec\\";
            var appPath ="app\\";
            processSettings = new ProcessSettings{
                Arguments = $"run -v {currentDirectory}:C:\\app {testContainerName} powershell -Command \"cd {appPath}{dockerTag.imageDirectory}; Invoke-Pester {specPath}{dockerTag.imageDirectory}* -OutputFile PesterTestResults.xml -OutputFormat NUnitXml -EnableExit\""
            };
        }

        using(var process = StartAndReturnProcess("docker", processSettings))
        {
            process.WaitForExit();
            // This should output 0 as valid arguments supplied
            Information("Exit code: {0}", process.GetExitCode());
            if (process.GetExitCode() > 0)
            {
                throw new Exception("Tests exited with exit code greater than 0");
            }
        }
    }
    catch (Exception e)
    {
        Information(e);
        throw; // rethrow the exception so cake will fail
    }
});

Task("Push")
    .IsDependentOn("Build")
    .IsDependentOn("Test")
    .Does(() =>
{
    try
    {
        Information("Releasing image to Docker Hub");
        dockerTag.Tags().ToList().ForEach((tag) => {
            Information("Releasing image to Docker Hub");
            using(var process = StartAndReturnProcess("docker", new ProcessSettings{ Arguments = $"push {tag}" }))
            {
                process.WaitForExit();
                // This should output 0 as valid arguments supplied
                Information("Exit code: {0}", process.GetExitCode());
                if (process.GetExitCode() > 0)
                {
                    throw new Exception("Pushing docker image failed");
                }
            }
        });

    } catch (Exception e)
    {
        Information(e);
        throw; // rethrow the exception so cake will fail
    }
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////
Task("Default")
    .IsDependentOn("Build")
    .IsDependentOn("Test");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////
RunTarget(target);
