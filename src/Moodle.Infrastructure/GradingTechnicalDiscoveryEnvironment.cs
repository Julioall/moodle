using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Infrastructure;

internal sealed class GradingTechnicalDiscoveryEnvironment(
    IOptions<MoodleApiOptions> moodleApiOptions,
    IOptions<AssignmentWriteFeatureOptions> featureOptions) : IGradingTechnicalDiscoveryEnvironment
{
    private readonly MoodleApiOptions _moodleApiOptions = moodleApiOptions.Value;
    private readonly AssignmentWriteFeatureOptions _featureOptions = featureOptions.Value;

    public bool AssignmentGradeWriteEnabled => _featureOptions.AssignmentGradeWriteEnabled;

    public bool AssignmentFeedbackWriteEnabled => _featureOptions.AssignmentFeedbackWriteEnabled;

    public bool AllowServiceTokenForReadOnlyQueries => _moodleApiOptions.AllowServiceTokenForReadOnlyQueries;
}
