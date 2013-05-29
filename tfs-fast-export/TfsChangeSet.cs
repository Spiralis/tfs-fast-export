using System;
using System.Collections.Generic;
using System.Configuration;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using System.IO;
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
	public class TfsChangeSet
	{
		private static Dictionary<string, Tuple<string, CommitCommand>> _Branches = new Dictionary<string, Tuple<string, CommitCommand>>();
		private static Dictionary<int, CommitCommand> _Commits = new Dictionary<int, CommitCommand>();

		private Changeset _ChangeSet;
		public TfsChangeSet(Changeset changeSet)
		{
			_ChangeSet = changeSet;
			@this = this;
		}

		// FYI this whole thing is entirely not thread-safe.
		private static TfsChangeSet @this;
		private static Dictionary<int, Func<CommitCommand>> _SpecialCommands = new Dictionary<int, Func<CommitCommand>>()
		{
			// use this to do checkin specific actions;  one example is when a branch itself changes name
            //{ 12345, () =>
            //    {
            //        _Branches["$/Branch-A/"] = _Branches["$/Branch-B/"];
            //        _Branches.Remove("$/Branch-A/");
            //        return null;
            //    } },
		};
		public CommitCommand ProcessChangeSet(bool usingStdOut)
		{
			if (_SpecialCommands.ContainsKey(_ChangeSet.ChangesetId))
				return _SpecialCommands[_ChangeSet.ChangesetId]();
			return DoProcessChangeSet(usingStdOut);
		}

		private List<FileCommand> fileCommands = new List<FileCommand>();
		private List<CommitCommand> merges = new List<CommitCommand>();
		private string branch = null;
		private Func<Change, bool> _WhereClause = (x) => true;
		private CommitCommand DoProcessChangeSet(bool usingStdOut)
		{
            // Get userInfo 
            Tuple<string, string> committerInfo = GetUserDisplayNameAndEmail(_ChangeSet.CommitterDisplayName, _ChangeSet.Committer);
			var committer = new CommitterCommand(committerInfo.Item1, committerInfo.Item2, _ChangeSet.CreationDate);
	
            AuthorCommand author = null;
            if (_ChangeSet.Owner != _ChangeSet.Committer)
            {
                Tuple<string, string> authorInfo = GetUserDisplayNameAndEmail(_ChangeSet.OwnerDisplayName, _ChangeSet.Owner);
                author = new AuthorCommand(authorInfo.Item1, authorInfo.Item2, _ChangeSet.CreationDate);
            }

			var orderedChanges = _ChangeSet.Changes
				.Where(_WhereClause)
				.Select((x, i) => new { x, i })
				.OrderBy(z => z.x.ChangeType)
				.ThenBy(z => z.i)
				.Select(z => z.x)
				.ToList();
			var deleteBranch = false;
			foreach (var change in orderedChanges)
			{
                if (!usingStdOut) Console.Write("."); // Show progress

				var path = GetPath(change.Item.ServerItem);
				if (path == null)
					continue;

				// we delete before we check folders in case we can delete
				// an entire subdir w/ one command instead of file by file
				if ((change.ChangeType & ChangeType.Delete) == ChangeType.Delete)
				{
					fileCommands.Add(new FileDeleteCommand(path));
					if (path == "")
					{
						deleteBranch = true;
						break;
					}
					continue;
				}

				if (change.Item.ItemType == ItemType.Folder)
					continue;

				if ((change.ChangeType & ChangeType.Rename) == ChangeType.Rename)
				{
					var vcs = change.Item.VersionControlServer;
					var history = vcs
						.QueryHistory(
							change.Item.ServerItem,
							new ChangesetVersionSpec(_ChangeSet.ChangesetId),
							change.Item.DeletionId,
							RecursionType.None,
							null,
							null,
							new ChangesetVersionSpec(_ChangeSet.ChangesetId),
							int.MaxValue,
							true,
							false)
						.OfType<Changeset>()
						.ToList();

					var previousChangeset = history[1];
					var previousFile = previousChangeset.Changes[0];
					var previousPath = GetPath(previousFile.Item.ServerItem);
					fileCommands.Add(new FileRenameCommand(previousPath, path));

					// remove delete commands, since rename will take care of biz
					fileCommands.RemoveAll(fc => fc is FileDeleteCommand && fc.Path == previousPath);
				}

				var blob = GetDataBlob(change.Item);
                if (blob != null)
                {
                    fileCommands.Add(new FileModifyCommand(path, blob));
                }
                else
                {
                    ; // The file that failed is ignored. Do nothing. 
                }

				if ((change.ChangeType & ChangeType.Branch) == ChangeType.Branch)
				{
					var vcs = change.Item.VersionControlServer;
					var history = vcs.GetBranchHistory(new[] { new ItemSpec(change.Item.ServerItem, RecursionType.None) }, new ChangesetVersionSpec(_ChangeSet.ChangesetId));

                    if (history.Any() && history[0].Any())
                    {
                        var itemHistory = history[0][0];
                        var mergedItem = FindMergedItem(itemHistory, _ChangeSet.ChangesetId);
                        var branchFrom = GetBranch(mergedItem.Relative.BranchFromItem.ServerItem);
                        if (branchFrom != null)
                        {
                            var branchFromInfo = branchFrom.Item2;
                            var previousCommit = branchFromInfo.Item2;
                            if (!merges.Contains(previousCommit))
                                merges.Add(previousCommit);
                        }
                        else
                        {
                            Console.Error.WriteLine("Unable to find the source of the branch-changeset: {0}, ServerItem: {1}", _ChangeSet.ChangesetId, mergedItem.Relative.BranchFromItem.ServerItem);
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("Unable to get proper history-info for the branch-changeset: {0}, ServerItem: {1}", _ChangeSet.ChangesetId, change.Item.ServerItem);
                    }
                }

				if ((change.ChangeType & ChangeType.Merge) == ChangeType.Merge)
				{
					var vcs = change.Item.VersionControlServer;
					var mergeHistory = vcs.QueryMergesExtended(new ItemSpec(change.Item.ServerItem, RecursionType.None), new ChangesetVersionSpec(_ChangeSet.ChangesetId), null, new ChangesetVersionSpec(_ChangeSet.ChangesetId)).ToList();
					foreach (var mh in mergeHistory)
					{
						var branchInfo = GetBranch(mh.SourceItem.Item.ServerItem).Item2;
						var previousCommit = branchInfo.Item2;
						if (!merges.Contains(previousCommit))
							merges.Add(previousCommit);
					}
				}
			}

			var reference = _Branches[branch];
			var commit = new CommitCommand(
				markId: _ChangeSet.ChangesetId,
				reference: reference.Item1,
				committer: committer,
				author: author,
				commitInfo: new DataCommand(_ChangeSet.Comment ?? ""),
				fromCommit: reference.Item2,
				mergeCommits: merges,
				fileCommands: fileCommands);
			_Commits[_ChangeSet.ChangesetId] = commit;

			if (deleteBranch)
				_Branches.Remove(branch);
			else
				_Branches[branch] = Tuple.Create(reference.Item1, commit);

			return commit;
		}

	    // 10,000,000 to get it out of way of normal checkins
		private static int _MarkID = 10000001;

	    private BlobCommand GetDataBlob(Item item)
		{
			var bytes = new byte[item.ContentLength];
	        BlobCommand blob = null;
            try
            {
                var str = item.DownloadFile();
                str.Read(bytes, 0, bytes.Length);
                str.Close();

                var id = _MarkID++;
                blob = BlobCommand.BuildBlob(bytes, id);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Warning: Unable to fetch file '{0}'{1}{2}", item.ServerItem, Environment.NewLine, ex);
                Console.Error.WriteLine();
            }
	        return blob;
		}

		private static BranchHistoryTreeItem FindMergedItem(BranchHistoryTreeItem parent, int changeSetId)
		{
			foreach (BranchHistoryTreeItem item in parent.Children)
			{
				if (item.Relative.IsRequestedItem)
					return item;

				var x = FindMergedItem(item, changeSetId);
				if (x != null)
					return x;
			}
			return null;
		}

		private Tuple<string, Tuple<string, CommitCommand>> GetBranch(string serverPath)
		{
			foreach (var x in _Branches)
				if (serverPath.StartsWith(x.Key))
					return Tuple.Create(x.Key, x.Value);
			return null;
		}

		private string GetPath(string serverPath)
		{
			if (branch == null)
			{
				var branchInfo = GetBranch(serverPath);
				if (branchInfo == null)
				{
					CreateNewBranch(serverPath);
					return "";
				}
				else
					branch = branchInfo.Item1;
			}

			if (!serverPath.StartsWith(branch))
				// for now ignore secondary branches and hope that other filemodify commands work this stuff out
				return null;

			return serverPath.Replace(branch, "");
		}

		private void CreateNewBranch(string serverPath)
		{
		    // If the whole collection is indexed, then use the use _ROOT as the starting point
            if (serverPath.Equals("$/")) serverPath = serverPath + "_ROOT";

            // Assumes that main directory for branch is the first thing added in new branch
			branch = serverPath + "/";
			if (!_Branches.ContainsKey(branch))
			{
				_Branches[branch] = Tuple.Create(string.Format("refs/heads/{0}", Path.GetFileName(serverPath)), default(CommitCommand));
				fileCommands.Add(new FileDeleteAllCommand());
			}
		}

		#region Active Directory
		private static string ProcessADName(string adName)
		{
			if (string.IsNullOrEmpty(adName))
				return "";

			if (!adName.Contains('\\'))
				return adName;

			var split = adName.Split('\\');
			return split[1];
		}

		private static UserPrincipal GetUserPrincipal(string userName)
		{
			var domainContext = new PrincipalContext(ContextType.Domain);
			var user = UserPrincipal.FindByIdentity(domainContext, IdentityType.SamAccountName, ProcessADName(userName));
			if (user != null)
				return user;
			throw new InvalidOperationException(string.Format("Cannot find current user ({0}) in any domains.", userName));
		}

        private static string MapDomain(string user)
        {
            var segments = user.Split('\\');
            var domain = segments[0].Trim();
            var userName = segments[1].Trim();
            return Program.DomainMap.ContainsKey(domain) ? string.Format("{0}\\{1}", Program.DomainMap[domain].Trim(), userName) : user;
        }

        private static Tuple<string, string> GetUserDisplayNameAndEmail(string tfsDisplayName, string tfsUser)
        {
            var actualAdUser = MapDomain(tfsUser);
            string emailAddress;
            string displayName;
            try
            {
                UserPrincipal userPrincipal = GetUserPrincipal(actualAdUser);
                emailAddress = userPrincipal.EmailAddress;
                displayName = userPrincipal.DisplayName;
            }
            catch
            {
                emailAddress = actualAdUser.Split('\\')[1] + Program.InactiveEmailUsernameExtension;
                displayName = tfsDisplayName.Equals(tfsUser) ? actualAdUser : tfsDisplayName;
            }

            return new Tuple<string, string>(displayName, emailAddress);
        }

        #endregion
	}
}
