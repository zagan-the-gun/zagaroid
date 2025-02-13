using System;
using UnityEngine;

namespace Twitcher
{
    public class FirstToVote : Vote
    {
        /// <summary>
        /// The number of votes needed for an option to win.
        /// </summary>
        public int Target { get; private set; }

        public override float Progress
        {
            get { return 0.0f; }
        }

        /// <summary>
        /// Creates a new instance of a FirstToVote
        /// </summary>
        /// <param name="votePrefix">Vote prefix that must be at the start of vote messages..</param>
        /// <param name="options">Options that can be voted on.</param>
        /// <param name="target">The number of votes needed for an option to win.</param>
        /// <param name="onComplete">Callback to give the results to on the votes conclusion.</param>
        public FirstToVote(string votePrefix, string[] options, int target, VoteResultsDelegate onComplete)
            : base(votePrefix, options, onComplete)
        {
            Target = target;
        }
    }
}
