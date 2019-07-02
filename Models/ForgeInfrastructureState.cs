using System.Collections.Generic;
using System.Runtime.Serialization;
using Fathym;
using LCU.Graphs.Registry.Enterprises.Provisioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LCU.State.API.Forge.Infrastructure.Models
{
    [DataContract]
    public class ForgeInfrastructureState
    {
        [DataMember]
        public virtual InfrastructureApplicationSeedState AppSeed { get; set; }

        [DataMember]
        public virtual DevOpsState DevOps { get; set; }

        [DataMember]
        public virtual Environment Environment { get; set; }

        [DataMember]
        public virtual MetadataModel EnvSettings { get; set; }

        [DataMember]
        public virtual string Error { get; set; }

        [DataMember]
        public virtual GitHubState GitHub { get; set; }

        [DataMember]
        public virtual bool InfrastructureConfigured { get; set; }

        [DataMember]
        public virtual InfrastructureTemplateState InfraTemplate { get; set; }

        [DataMember]
        public virtual bool Loading { get; set; }

        [DataMember]
        public virtual bool ProductionConfigured { get; set; }

        [DataMember]
        [JsonConverter(typeof(StringEnumConverter))]
        public virtual ForgeInfrastructureSetupStepTypes? SetupStep { get; set; }

        [DataMember]
        public virtual bool SourceControlConfigured { get; set; }
    }

    [DataContract]
    public class DevOpsState
    {
        [DataMember]
        public virtual bool Configured { get; set; }

        [DataMember]
        public virtual string NPMAccessToken { get; set; }

        [DataMember]
        public virtual string NPMRegistry { get; set; }

        [DataMember]
        public virtual string ProjectName { get; set; }

        [DataMember]
        public virtual bool Setup { get; set; }

        [DataMember]
        public virtual string Unauthorized { get; set; }
    }

    [DataContract]
    public class GitHubState
    {
        [DataMember]
        public virtual bool OAuthConfigured { get; set; }

        [DataMember]
        public virtual List<Octokit.Organization> Organizations { get; set; }

        [DataMember]
        public virtual List<Octokit.Repository> OrgRepos { get; set; }

        [DataMember]
        public virtual string SelectedOrg { get; set; }
    }

    [DataContract]
    public class InfrastructureTemplateState
    {
        [DataMember]
        public virtual List<string> Options { get; set; }

        [DataMember]
        public virtual string SelectedTemplate { get; set; }
    }

    [DataContract]
    public class InfrastructureApplicationSeedState
    {
        [DataMember]
        public virtual bool AppSeedBuilt { get; set; }
    
        [DataMember]
        public virtual bool AppSeeded { get; set; }
    
        [DataMember]
        public virtual bool HasBuild { get; set; }
    
        [DataMember]
        public virtual bool InfraBuilt { get; set; }
    
        [DataMember]
        public virtual string NewName { get; set; }
    
        [DataMember]
        public virtual List<InfrastructureApplicationSeedOption> Options { get; set; }

        [DataMember]
        public virtual string SelectedSeed { get; set; }
    
        [DataMember]
        [JsonConverter(typeof(StringEnumConverter))]
        public virtual ForgeInfrastructureApplicationSeedStepTypes? Step { get; set; }
    }

    [DataContract]
    public class InfrastructureApplicationSeedOption
    {
        [DataMember]
        public virtual List<string> Commands { get; set; }

        [DataMember]
        public virtual string Description { get; set; }

        [DataMember]
        public virtual string ImageSource { get; set; }

        [DataMember]
        public virtual string Lookup { get; set; }

        [DataMember]
        public virtual string Name { get; set; }

        [DataMember]
        public virtual string ReleasePackageSuffix { get; set; }

        [DataMember]
        public virtual InfrastructureApplicationSeedFork SeedFork { get; set; }
    }

    [DataContract]
    public class InfrastructureApplicationSeedFork
    {
        [DataMember]
        public virtual string Organization { get; set; }

        [DataMember]
        public virtual string Repository { get; set; }
    }

    [DataContract]
    public enum ForgeInfrastructureSetupStepTypes
    {
        [EnumMember]
        Azure,

        [EnumMember]
        AWS,

        [EnumMember]
        Custom
    }

    [DataContract]
    public enum ForgeInfrastructureApplicationSeedStepTypes
    {
        [EnumMember]
        Creating,

        [EnumMember]
        Created
    }
}