﻿using System;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using NuGet.Packaging;
using NuGet.Versioning;

namespace GprTool
{
    public class NuGetUtilities
    {
        public static Manifest ReadNupkgManifest(string nupkgPath)
        {
            if (nupkgPath == null) throw new ArgumentNullException(nameof(nupkgPath));
            using var packageArchiveReader = new PackageArchiveReader(nupkgPath.ReadSharedToStream());
            return Manifest.ReadFrom(packageArchiveReader.GetNuspec(), false);
        }

        public static bool ShouldRewriteNupkg(string nupkgPath, string repositoryUrl, NuGetVersion nuGetVersion = null)
        {
            if (nupkgPath == null) throw new ArgumentNullException(nameof(nupkgPath));

            using var packageArchiveReader = new PackageArchiveReader(nupkgPath.OpenReadShared(), false);

            var nuspecXDocument = packageArchiveReader.NuspecReader.Xml;
            var packageXElement = nuspecXDocument.Single("package");
            var metadataXElement = packageXElement.Single("metadata");
            var versionXElement = metadataXElement.Single("version");

            if (!NuGetVersion.TryParse(versionXElement.Value, out var nuspecVersion)
                || nuGetVersion != null && !nuGetVersion.Equals(nuspecVersion))
            {
                return true;
            }

            var repositoryXElement = metadataXElement.SingleOrDefault("repository");
            if (repositoryXElement == null)
            {
                return true;
            }

            var nuspecRepositoryUrl = repositoryXElement.Attribute("url")?.Value;
            
            return !string.Equals(repositoryUrl, nuspecRepositoryUrl, StringComparison.Ordinal);
        }

        public static string RewriteNupkg(string nupkgPath, string repositoryUrl, NuGetVersion nuGetVersion = null)
        {
            if (nupkgPath == null) throw new ArgumentNullException(nameof(nupkgPath));
            if (repositoryUrl == null) throw new ArgumentNullException(nameof(repositoryUrl));

            var randomDirectoryId = Guid.NewGuid().ToString("N");
            var nupkgFilename = Path.GetFileName(nupkgPath);
            var nupkgFilenameWithoutExt = Path.GetFileNameWithoutExtension(nupkgFilename);
            var nupkgWorkingDirectoryAbsolutePath = Path.GetDirectoryName(nupkgPath);
            var workingDirectory = Path.Combine(nupkgWorkingDirectoryAbsolutePath, $"{nupkgFilenameWithoutExt}_{randomDirectoryId}");

            using var tmpDirectory = new DisposableDirectory(workingDirectory);
            using var packageArchiveReader = new PackageArchiveReader(nupkgPath.ReadSharedToStream(), false);
            using var nuspecMemoryStream = new MemoryStream();

            var nuspecXDocument = packageArchiveReader.NuspecReader.Xml;
            var packageXElement = nuspecXDocument.Single("package");
            var metadataXElement = packageXElement.Single("metadata");
            var packageId = packageXElement.Single("id").Value;
            var versionXElement = metadataXElement.Single("version");
            
            if (nuGetVersion != null)
            {
                versionXElement.SetValue(nuGetVersion); 
            }
            else
            {
                nuGetVersion = NuGetVersion.Parse(versionXElement.Value);
            }

            var repositoryXElement = metadataXElement.SingleOrDefault("repository");
            if (repositoryXElement == null)
            {
                repositoryXElement = new XElement("repository");
                repositoryXElement.SetAttributeValue("url", repositoryUrl);
                repositoryXElement.SetAttributeValue("type", "git");
                metadataXElement.Add(repositoryXElement);
            }
            else
            {
                repositoryXElement.SetAttributeValue("url", repositoryUrl);
                repositoryXElement.SetAttributeValue("type", "git");
            }
            
            nuspecXDocument.Save(nuspecMemoryStream);
            nuspecMemoryStream.Seek(0, SeekOrigin.Begin);

            ZipFile.ExtractToDirectory(nupkgPath, tmpDirectory.WorkingDirectory, true);

            var nuspecDstFilename = Path.Combine(tmpDirectory.WorkingDirectory, $"{packageId}.nuspec");
            File.WriteAllBytes(nuspecDstFilename, nuspecMemoryStream.ToArray());

            using var outputStream = new MemoryStream();
            
            var packageBuilder = new PackageBuilder(nuspecMemoryStream, tmpDirectory.WorkingDirectory, propertyProvider => throw new NotImplementedException());
            packageBuilder.Save(outputStream);

            var nupkgDstFilenameAbsolutePath = Path.Combine(nupkgWorkingDirectoryAbsolutePath, $"{packageId}.{nuGetVersion}_gpr.nupkg");

            File.WriteAllBytes(nupkgDstFilenameAbsolutePath, outputStream.ToArray());

            return nupkgDstFilenameAbsolutePath;
        }

        public static string FindTokenInNuGetConfig(Action<string> warning = null)
        {
            var configFile = GetDefaultConfigFile(warning);
            if (!File.Exists(configFile))
            {
                warning?.Invoke($"Couldn't find file at '{configFile}'");
                return null;
            }

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configFile);
            var tokenValue = xmlDoc.SelectSingleNode("/configuration/packageSourceCredentials/github/add[@key='ClearTextPassword']/@value");
            if (tokenValue == null)
            {
                warning?.Invoke($"Couldn't find a personal access token for GitHub in:");
                warning?.Invoke(configFile);
                warning?.Invoke("");
                warning?.Invoke("Please generate a token with 'repo', 'write:packages', 'read:packages' and 'delete:packages' scopes:");
                warning?.Invoke("https://github.com/settings/tokens");
                warning?.Invoke("");
                warning?.Invoke(@"The token can be added under the 'configuration' element of your NuGet.Config file:
<packageSourceCredentials>
  <github>
    <add key=""Username"" value=""USERNAME"" />
    <add key=""ClearTextPassword"" value=""TOKEN"" />
  </github>
</packageSourceCredentials>
");

                return null;
            }

            return tokenValue.Value;
        }

        public static void SetApiKey(string configFile, string token, string source, Action<string> warning = null)
        {
            var xmlDoc = new XmlDocument();
            if (File.Exists(configFile))
            {
                xmlDoc.Load(configFile);
            }

            SetApiKey(xmlDoc, token, source);

            var dir = Path.GetDirectoryName(configFile);
            if (!Directory.Exists(dir))
            {
                warning?.Invoke($"Creating directory: {dir}");
                Directory.CreateDirectory(dir);
            }

            warning?.Invoke($"Saving file to: {configFile}");
            xmlDoc.Save(configFile);
            warning?.Invoke(File.ReadAllText(configFile));
        }

        public static void SetApiKey(XmlDocument xmlDoc, string token, string source)
        {
            var configurationElement = xmlDoc.SelectSingleNode("/configuration") ?? xmlDoc.CreateElement("configuration");
            var packageSourceCredentialsElement = configurationElement.SelectSingleNode("packageSourceCredentials") ?? xmlDoc.CreateElement("packageSourceCredentials");
            var sourceElement = packageSourceCredentialsElement.SelectSingleNode(source) ?? xmlDoc.CreateElement(source);
            sourceElement.RemoveAll();
            var addUsernameElement = xmlDoc.CreateElement("add");
            addUsernameElement.SetAttribute("key", "Username");
            addUsernameElement.SetAttribute("value", "PersonalAccessToken");
            var addClearTextPasswordElement = xmlDoc.CreateElement("add");
            addClearTextPasswordElement.SetAttribute("key", "ClearTextPassword");
            addClearTextPasswordElement.SetAttribute("value", token);
            sourceElement.AppendChild(addUsernameElement);
            sourceElement.AppendChild(addClearTextPasswordElement);
            packageSourceCredentialsElement.AppendChild(sourceElement);
            configurationElement.AppendChild(packageSourceCredentialsElement);
            xmlDoc.AppendChild(configurationElement);
        }

        public static string GetDefaultConfigFile(Action<string> warning = null)
        {
            string baseDir;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            else
            {
                var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                baseDir = Path.Combine(userDir, ".nuget");
            }

            return Path.Combine(baseDir, "NuGet", "NuGet.Config");
        }

    }

    public class DisposableDirectory : IDisposable
    {
        public string WorkingDirectory { get; }
        
        public DisposableDirectory(string workingDirectory)
        {
            WorkingDirectory = workingDirectory;
            Directory.CreateDirectory(workingDirectory);
        }
        
        public void Dispose()
        {
            Directory.Delete(WorkingDirectory, true);
        }
    }
}
