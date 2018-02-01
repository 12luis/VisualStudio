﻿using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;
using GitHub.Models;
using GitHub.ViewModels.GitHubPane;
using ReactiveUI;

namespace GitHub.SampleData
{
    public class PullRequestReviewViewModelDesigner : PanePageViewModelBase, IPullRequestReviewViewModel
    {
        public PullRequestReviewViewModelDesigner()
        {
            PullRequestNumber = 734;

            Model = new PullRequestReviewModel
            {
                User = new AccountDesigner { Login = "Haacked", IsUser = true },
            };

            State = "approved";
            Body = @"Just a few comments. I don't feel too strongly about them though.

Otherwise, very nice work here! ✨";
            Files = new PullRequestFilesViewModelDesigner();
            CommentCount = 3;

            FileComments = new[]
            {
                new PullRequestReviewCommentModel
                {
                    Body = @"These should probably be properties. Most likely they should be readonly properties. I know that makes creating instances of these not look as nice as using property initializers when constructing an instance, but if these properties should never be mutated after construction, then it guides future consumers to the right behavior.

However, if you're two-way binding these properties to a UI, then ignore the readonly part and make them properties. But in that case they should probably be reactive properties (or implement INPC).",
                    Path = "src/GitHub.Exports.Reactive/ViewModels/IPullRequestListViewModel.cs",
                    Position = 1,
                },
                new PullRequestReviewCommentModel
                {
                    Body = "While I have no problems with naming a variable ass I think we should probably avoid swear words in case Microsoft runs their Policheck tool against this code.",
                    Path = "src/GitHub.App/ViewModels/PullRequestListViewModel.cs",
                    Position = 1,
                },
            };

            OutdatedFileComments = new[]
            {
                new PullRequestReviewCommentModel
                {
                    Body = @"So this is just casting a mutable list to an IReadOnlyList which can be cast back to List.",
                    Path = "src/GitHub.App/ViewModels/PullRequestListViewModel.cs",
                    Position = null,
                }
            };
        }

        public ILocalRepositoryModel LocalRepository { get; set; }
        public string RemoteRepositoryOwner { get; set; }
        public int PullRequestNumber { get; set; }
        public long PullRequestReviewId { get; set; }
        public IPullRequestReviewModel Model { get; set; }
        public string State { get; set; }
        public bool IsPending { get; set; }
        public string Body { get; set; }
        public IPullRequestFilesViewModel Files { get; set; }
        public IReadOnlyList<IPullRequestReviewCommentModel> FileComments { get; set; }
        public IReadOnlyList<IPullRequestReviewCommentModel> OutdatedFileComments { get; set; }
        public int CommentCount { get; set; }
        public ReactiveCommand<Unit> OpenComment { get; }
        public ReactiveCommand<object> NavigateToPullRequest { get; }
        public ReactiveCommand<Unit> Submit { get; }

        public Task InitializeAsync(
            ILocalRepositoryModel localRepository,
            IConnection connection,
            string owner,
            string repo,
            int pullRequestNumber,
            long pullRequestReviewId)
        {
            return Task.CompletedTask;
        }

        public Task InitializeNewAsync(ILocalRepositoryModel localRepository, IConnection connection, string owner, string repo, int pullRequestNumber)
        {
            return Task.CompletedTask;
        }

        public Task Load(IPullRequestModel pullRequest)
        {
            return Task.CompletedTask;
        }
    }
}
