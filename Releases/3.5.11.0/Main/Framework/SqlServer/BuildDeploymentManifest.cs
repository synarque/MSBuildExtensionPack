﻿//-----------------------------------------------------------------------
// <copyright file="BuildDeploymentManifest.cs">(c) http://www.codeplex.com/MSBuildExtensionPack. This source is subject to the Microsoft Permissive License. See http://www.microsoft.com/resources/sharedsource/licensingbasics/sharedsourcelicenses.mspx. All other rights reserved.</copyright>
// This task is based on code from (http://sqlsrvintegrationsrv.codeplex.com/). It is used here with permission.
//-----------------------------------------------------------------------
// TODO: Need to fix 3 stylecop errors here.
// <autogenerated/>
namespace MSBuild.ExtensionPack.SqlServer
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Security.Principal;
    using System.Xml.Linq;
    using Microsoft.Build.Framework;
    using Microsoft.Build.Utilities;

    /// <summary>
    /// This Task can be used to translate SSIS projects (.dtproj  files) into an SSIS deployment manifest 
    /// <para />
    /// <para><b>Remote Execution Support:</b> NA</para>
    /// </summary>
    /// <example>
    /// <code lang="xml"><![CDATA[
    /// <Project ToolsVersion="3.5" DefaultTargets="Default" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
    ///     <PropertyGroup>
    ///         <TPath>$(MSBuildProjectDirectory)\..\MSBuild.ExtensionPack.tasks</TPath>
    ///         <TPath Condition="Exists('$(MSBuildProjectDirectory)\..\..\Common\MSBuild.ExtensionPack.tasks')">$(MSBuildProjectDirectory)\..\..\Common\MSBuild.ExtensionPack.tasks</TPath>
    ///     </PropertyGroup>
    ///     <Import Project="$(TPath)"/>
    ///     <Target Name="Default">
    ///         <PropertyGroup>
    ///             <OutputRoot>C:\a\</OutputRoot>
    ///         </PropertyGroup>
    ///         <ItemGroup>
    ///             <SSISProjectFile Include="C:\MyPackages.dtproj"/>
    ///             <SSISProject Include="@(SSISProjectFile)">
    ///                 <OutputDirectory>$(OutputRoot)%(FileName)</OutputDirectory>
    ///             </SSISProject>
    ///         </ItemGroup>
    ///         <MSBuild.ExtensionPack.SqlServer.BuildDeploymentManifest InputProject="@(SSISProject)"/>
    ///     </Target>
    /// </Project>
    /// ]]></code>    
    /// </example>  
    [HelpUrl("http://www.msbuildextensionpack.com/help/3.5.11.0/html/581fb9b8-67dd-ec9d-8c61-77e0d202993c.htm")]
    public class BuildDeploymentManifest : Task
    {
        private bool allowConfigurationChanges = true;

        /// <summary>
        /// Each .dtproj is a separate item in an ItemGroup, but each item needs a custom OutputDirectory metadata value that specifies where the deployment manifest should be written and project files copied to
        /// </summary>
        [Required]
        public ITaskItem[] InputProject { get; set; }

        /// <summary>
        /// Set to true to show the Configure Package dialog box after copying files. Default is true.
        /// </summary>
        public bool AllowConfigurationChanges
        {
            get { return this.allowConfigurationChanges; }
            set { this.allowConfigurationChanges = value; }
        }

        // TODO: Need to reduce the cyclomatic complexity of this.
        public override bool Execute()
        {
            XNamespace dts = "www.microsoft.com/SqlServer/Dts";

            // MSBuild itemgroups split multiple files by ;.
            // Each project file becomes a manifest file.
            foreach (ITaskItem file in this.InputProject)
            {
                if (!File.Exists(file.ItemSpec))
                {
                    this.Log.LogError(string.Format(CultureInfo.InvariantCulture, "File not found: {0}", file.ItemSpec));
                    return false;
                }

                XDocument document = this.LoadAndLog(file.ItemSpec);
                string outputDirectory = file.GetMetadata("OutputDirectory");
                string projectBase = Path.GetDirectoryName(file.ItemSpec);
                if (string.IsNullOrEmpty(outputDirectory))
                {
                    Log.LogError("No output directory specified for {0}", file.ItemSpec);
                    return false;
                }

                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                WindowsIdentity currentIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (currentIdentity == null)
                {
                    this.Log.LogError("Failed to get current Windows Identity");
                    return false;
                }

                // one ginormous LINQ query to build the deployment manifest -- technically one line of code...
                new XElement(
                    "DTSDeploymentManifest",
                    //// basic attributes for the manifest, made to look like it came out of SSIS
                    new XAttribute("AllowConfigurationChanges", this.AllowConfigurationChanges),
                    new XAttribute("GeneratedBy", currentIdentity.Name),
                    new XAttribute("GeneratedFromProjectName", Path.GetFileNameWithoutExtension(file.ItemSpec)),
                    new XAttribute("GeneratedDate", System.DateTime.UtcNow),
                    //// add all the packages
                    from a in document.Descendants("FullPath")
                    where a.Parent.Name == "DtsPackage"
                    select this.CreateElementForFileAndCopy("Package", Path.Combine(projectBase, a.Value), outputDirectory),
                    //// now, for each of the packages, load them and suck out config file references
                    from a in document.Descendants("FullPath")
                    let packageFileName = Path.Combine(Path.GetDirectoryName(file.ItemSpec), a.Value)
                    where a.Parent.Name == "DtsPackage"
                    from f in this.LoadAndLog(packageFileName).Descendants(dts + "Property")
                    where f.Parent.Name == dts + "Configuration" &&
                        f.Attribute(dts + "Name") != null &&
                        f.Attribute(dts + "Name").Value == "ConfigurationString" &&
                        f.Parent.Descendants().Any(x => x.Attribute(dts + "Name") != null &&
                            x.Attribute(dts + "Name").Value == "ConfigurationType" && x.Value == "1")
                    select this.CreateElementForFileAndCopy("ConfigurationFile", Path.Combine(projectBase, f.Value), outputDirectory),
                    //// miscellaneous files
                    from a in document.Descendants("FullPath")
                    where a.Parent.Name == "ProjectItem" && a.Parent.Parent.Name == "Miscellaneous"
                    select this.CreateElementForFileAndCopy("MiscellaneousFile", Path.Combine(projectBase, a.Value), outputDirectory)).Save(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(file.ItemSpec) + ".SSISDeploymentManifest"));
            }

            return true;
        }

        private XDocument LoadAndLog(string fileName)
        {
            this.Log.LogMessage("Reading File {0}", fileName);
            return XDocument.Load(fileName);
        }

        private XElement CreateElementForFileAndCopy(string fileType, string sourcePath, string destinationPath)
        {
            string destinationFile = Path.Combine(destinationPath, Path.GetFileName(sourcePath));
            Log.LogMessage("Copying {0} {1} to {2}", fileType, sourcePath, destinationFile);
            bool changedAttribute = false;
            if (File.Exists(destinationFile))
            {
                // First make sure the file is writable.
                FileAttributes fileAttributes = System.IO.File.GetAttributes(destinationFile);

                // If readonly attribute is set, reset it.
                if ((fileAttributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    System.IO.File.SetAttributes(destinationFile, fileAttributes ^ FileAttributes.ReadOnly);
                    changedAttribute = true;
                }
            }

            File.Copy(sourcePath, destinationFile, true);

            // if we changed the file attribute, set it back
            if (changedAttribute)
            {
                System.IO.File.SetAttributes(destinationFile, FileAttributes.ReadOnly);
            }

            return new XElement(fileType, Path.GetFileName(sourcePath));
        }
    }
}
