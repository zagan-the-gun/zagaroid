using System;
using UnityEngine;

namespace Twitcher
{
    public class TimedVote : Vote
    {
        private DateTime startTime;


        public override float Progress
        {
            get
            {
                return Mathf.Clamp01(SecondsElapsed / Duration);
            }
        }

        /// <summary>
        /// Seconds elapsed since the vote was started in realtime.
        /// </summary>
        public float SecondsElapsed
        {
            get
            {
                return (float)((DateTime.Now - startTime).TotalSeconds);
            }
        }

        public float Duration { get; private set; }

        /// <summary>
        /// Creates a timed vote instance.
        /// </summary>
        /// <param name="votePrefix">Vote prefix that must be at the start of vote messages..</param>
        /// <param name="options">Options that can be voted on.</param>
        /// <param name="duration">Duration of the vote in seconds.</param>
        /// <param name="onComplete">Callback to give the results to on the votes conclusion.</param>
        public TimedVote(string votePrefix, string[] options, float duration, VoteResultsDelegate onComplete)
            : base(votePrefix, options, onComplete)
        {
            Duration = duration;
        }

        protected override void OnVoteStart()
        {
            base.OnVoteStart();
            startTime = DateTime.Now;
        }
    }
}
