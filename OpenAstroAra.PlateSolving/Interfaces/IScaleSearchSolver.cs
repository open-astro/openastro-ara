namespace OpenAstroAra.PlateSolving.Interfaces {

    /// <summary>#1136 — a solver that honours <see cref="PlateSolveParameter.SearchScale"/> (ASTAP's
    /// <c>-fov 0</c>). The staged search only runs its scale-search stage against one of these; for any
    /// other solver the stage would repeat the identical hinted solve and burn a timeout.</summary>
    public interface IScaleSearchSolver : IPlateSolver {
    }
}
