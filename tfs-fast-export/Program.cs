using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Management;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.TeamFoundation;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Framework;
using Microsoft.TeamFoundation.VersionControl.Client;

using fast_export;

namespace tfs_fast_export
{
	class Program
	{
        internal static Dictionary<string, string> DomainMap;
        internal static Dictionary<string, string> MailDomainMap;
        internal static string InactiveEmailUsernameExtension;

	    private static readonly HashSet<int> SkipCommits = new HashSet<int>();

		private static readonly HashSet<int> BreakCommits = new HashSet<int>()
		{
			// use this for debugging when you want to stop at a particular checkin for analysis
		};

		static void Main(string[] args)
		{
		    var outFile = ConfigurationManager.AppSettings["OutFile"];
		    var usingStdOut = String.IsNullOrWhiteSpace(outFile);
            var outStream = usingStdOut
		                        ? Console.OpenStandardOutput()
		                        : new FileStream(outFile, FileMode.Create);

            if (!usingStdOut) Console.WriteLine("========================================================================================================================");
            if (!usingStdOut) Console.Write("Creating git-clone of TFS repository:");
            
            var tfsProjectCollection = ConfigurationManager.AppSettings["TfsTeamProjectCollection"];
		    if (String.IsNullOrWhiteSpace(tfsProjectCollection))
		        throw new ConfigurationErrorsException(
		            "Error: Missing required setting for TfsTeamProjectCollection in the .exe.config file.");
		    
            var tfsRoot = ConfigurationManager.AppSettings["TfsRoot"];
		    if (String.IsNullOrWhiteSpace(tfsRoot))
		        throw new ConfigurationErrorsException(
                    "Error: Missing required setting for TfsRoot in the .exe.config file.");

            var skipCommitsSetting = ConfigurationManager.AppSettings["SkipCommits"];
            if (!String.IsNullOrWhiteSpace(skipCommitsSetting))
            {
                skipCommitsSetting.Split(new[] {' ', ',', ';'}, StringSplitOptions.RemoveEmptyEntries)
                                  .ToList()
                                  .ForEach(i => SkipCommits.Add(Int32.Parse(i)));
            }

            if (!usingStdOut) Console.WriteLine("{0}/{1}", tfsProjectCollection, tfsRoot);
            if (!usingStdOut) Console.WriteLine("Fetching list of changesets from TFS...");

			var collection = new TfsTeamProjectCollection(new Uri(tfsProjectCollection));
			collection.EnsureAuthenticated();
			var versionControl = collection.GetService<VersionControlServer>();

			var allChanges = versionControl
				.QueryHistory(
					tfsRoot,
					VersionSpec.Latest,
					0,
					RecursionType.Full,
					null,
					new ChangesetVersionSpec(1),
					VersionSpec.Latest,
					int.MaxValue,
					true,
					false)
				.OfType<Changeset>()
				.OrderBy(x => x.ChangesetId)
				.ToList();

		    var processed = 0;
		    var lastChangesetId = allChanges.Last().ChangesetId;
		    var sumChanges = allChanges.Sum(x => x.Changes.Count());


            if (!usingStdOut)
            {
                Console.WriteLine("\tFirst changeset-id..: {0:######}", allChanges.First().ChangesetId);
                Console.WriteLine("\tLast  changeset-id..: {0:######}", lastChangesetId);
                Console.WriteLine("\tNo of changesets....: {0:######}", allChanges.Count);
                Console.WriteLine("\tNo of actual changes: {0:######}", sumChanges);
                Console.WriteLine("------------------------------------------------------------------------------------------------------------------------");
            }

            CreateMapDomainList();
            CreateMapMailDomainList();
            InactiveEmailUsernameExtension = ConfigurationManager.AppSettings["InactiveEmailUsernameExtension"];

            foreach (var changeSet in allChanges)
            {
                var beforeProcessing = processed;
                processed += changeSet.Changes.Count();

                if (SkipCommits.Contains(changeSet.ChangesetId))
				{
                    if (!usingStdOut) Console.WriteLine("Skipping configuratively excluded changeset: {0}", changeSet.ChangesetId);
                    continue;
				}

                if (!usingStdOut) Console.Write("Progress: {1,6:##0.00%} Changeset: {0,6} > ", changeSet.ChangesetId, ((float)beforeProcessing) / sumChanges);
                
                if (BreakCommits.Contains(changeSet.ChangesetId))
				{
				    System.Diagnostics.Debugger.Break();
				}

				var commit = new TfsChangeSet(changeSet).ProcessChangeSet(usingStdOut);
				if (commit == null)
				{
                    if (!usingStdOut) Console.WriteLine(" Ops! Skipping 'null-commit' changeset.");
                    continue;
				}

                if (!usingStdOut) Console.WriteLine(".");
                outStream.RenderCommand(commit);
				outStream.WriteLine(string.Format("progress {0}/{1}", changeSet.ChangesetId, lastChangesetId));
            }
			outStream.WriteLine("done");
			outStream.Close();
            if (!usingStdOut) Console.WriteLine("========================================================================================================================");
		}

        private static void CreateMapDomainList()
        {
            // Map unactive domains to new domains
            var msDomainMap = ConfigurationManager.AppSettings["MsDomainMap"];
            var rawEntries = msDomainMap.Split(',');
            DomainMap = rawEntries.Select(entry => entry.Split('=')).ToDictionary(e => e[0], e => e[1]);
        }

        private static void CreateMapMailDomainList()
        {
            // Map unactive mail-domains to new domains
            var msDomainMap = ConfigurationManager.AppSettings["MailDomainMap"];
            var rawEntries = msDomainMap.Split(',');
            MailDomainMap = rawEntries.Select(entry => entry.Split('=')).ToDictionary(e => e[0], e => e[1]);
        }
    }
}
