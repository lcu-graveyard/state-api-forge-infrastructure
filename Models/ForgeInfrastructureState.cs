using System.Collections.Generic;
using System.Runtime.Serialization;
using Fathym;
using LCU.Graphs.Registry.Enterprises.Provisioning;
using LCU.Presentation.Personas.Applications;
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
        public virtual string EnterpriseName { get; set; }

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
        public virtual bool OAuthConfigured { get; set; }

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
        public virtual List<MetadataModel> Organizations { get; set; }

        [DataMember]
        public virtual List<MetadataModel> OrgRepos { get; set; }

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
}