using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Twitcher
{
    /// <summary>
    /// Interface for rate limiting.
    /// </summary>
    public interface IRateLimiter
    {
        bool HasTokens(int count = 1);
        void ConsumeTokens(int count = 1);
    }

    /// <summary>
    /// Limiter that provides unlimited rates.
    /// </summary>
    public class UnlimitedRate : IRateLimiter
    {
        public bool HasTokens(int count = 1) { return true; }
        public void ConsumeTokens(int count = 1) { }
    }

    /// <summary>
    /// Token based rate limiter used for Twitch due to the restrictions they impose on message sending.
    /// </summary>
    public class RateLimiter : IRateLimiter
    {
        private readonly int maxTokens;
        private readonly float secondsPerToken;
        private int tokenCount;
        private DateTime lastTokenTime;

        /// <summary>
        /// Construct a new rate limiter.
        /// </summary>
        /// <param name="maxTokens">Maximum token count.</param>
        /// <param name="secondsPerToken">Time in seconds for token regeneration.</param>
        public RateLimiter(int maxTokens, float secondsPerToken)
        {
            this.secondsPerToken = secondsPerToken;
            this.maxTokens = maxTokens;

            tokenCount = maxTokens;
            lastTokenTime = DateTime.Now;
        }

        /// <summary>
        /// Checks to see if the limiter has tokens for sending.
        /// </summary>
        /// <returns>True if limiter has enough tokens, false otherwise.</returns>
        public bool HasTokens(int count = 1)
        {
            int secondsSinceLast = Mathf.FloorToInt((float)(DateTime.Now - lastTokenTime).TotalSeconds);
            int tokensToAdd = (int)(secondsSinceLast / secondsPerToken);
            if (tokensToAdd > 0)
            {
                lastTokenTime.AddSeconds(tokensToAdd * secondsPerToken);
                tokenCount = Mathf.Min(tokenCount + tokensToAdd, maxTokens);
            }
            return tokenCount >= count;
        }

        /// <summary>
        /// Consumes tokens.
        /// </summary>
        /// <param name="count">Number of tokens to consume.</param>
        public void ConsumeTokens(int count = 1)
        {
            tokenCount -= count;
        }
    }
}