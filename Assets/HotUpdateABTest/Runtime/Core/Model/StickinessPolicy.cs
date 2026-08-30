namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// What happens to a user's variant when the experiment's weights change underneath them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither extreme is right. Pure hash bucketing is stateless and elegant, but changing a weight moves
    /// the variant boundary and therefore flips some users from one arm to the other. A user who has
    /// already seen the treatment and then flips is counted in both arms, which contaminates the result,
    /// and sees the product change under them for no reason. Pinning every assignment forever avoids that
    /// but freezes the experiment: ramping from five percent to fifty would only ever draw the new traffic
    /// from users who had never opened the app.
    /// </para>
    /// <para>
    /// <see cref="StickyAfterExposure"/> is the useful middle. Assignment stays stateless and free until
    /// the user actually sees the treated surface; that first exposure writes a pin which is honoured for
    /// the rest of the experiment's life. The invariant it buys is the one that matters - no user who has
    /// been treated ever switches arms - while users who have contributed nothing to the analysis are
    /// re-bucketed freely, so ramping works. <see cref="Stateless"/> is kept because the reshuffle is worth
    /// being able to demonstrate, and because an experiment whose surface is never "seen" in a meaningful
    /// sense has nothing to pin.
    /// </para>
    /// </remarks>
    public enum StickinessPolicy
    {
        /// <summary>Always re-bucket from the hash. Weight changes move users between arms.</summary>
        Stateless,

        /// <summary>Re-bucket freely until the first exposure; pin from then on.</summary>
        StickyAfterExposure
    }
}
