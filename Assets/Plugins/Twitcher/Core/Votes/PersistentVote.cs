using System;
using UnityEngine;

namespace Twitcher
{
    public class PersistentVote : Vote
    {
        public override float Progress { get { return 0; } }

        /// <summary>
        /// Creates an instance of a persistant vote, these votes only end when EndVote is called.
        /// </summary>
        /// <param name="votePrefix">Vote prefix that must be at the start of vote messages..</param>
        /// <param name="options">Options that can be voted on.</param>
        /// <param name="onComplete">Callback to give the results to on the votes conclusion.</param>
        internal PersistentVote(string votePrefix, string[] options, VoteResultsDelegate onComplete)
            : base(votePrefix, options, onComplete)
        {
        }
    }
}
