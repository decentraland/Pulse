using Microsoft.Extensions.Options;
using Pulse.InterestManagement;

namespace DCLPulseTests;

[TestFixture]
public class SpatialHashAreaOfInterestOptionsValidatorTests
{
    private SpatialHashAreaOfInterestOptionsValidator validator;

    [SetUp]
    public void SetUp()
    {
        validator = new SpatialHashAreaOfInterestOptionsValidator();
    }

    [Test]
    public void ValidConfig_Succeeds()
    {
        var options = MakeOptions(cellSize: 50f, scanCellRadius: 2, maxRadius: 100f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public void Coverage_ExactlyAtBoundary_Succeeds()
    {
        var options = MakeOptions(cellSize: 50f, scanCellRadius: 3, maxRadius: 150f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public void Coverage_BelowMaxRadius_Fails_WithSuggestedRadius()
    {
        var options = MakeOptions(cellSize: 50f, scanCellRadius: 1, maxRadius: 150f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.FailureMessage, Does.Contain("ScanCellRadius (1)"));
        Assert.That(result.FailureMessage, Does.Contain("CellSize (50)"));
        Assert.That(result.FailureMessage, Does.Contain("MaxRadius (150)"));
        Assert.That(result.FailureMessage, Does.Contain(">= 3"));
        Assert.That(result.FailureMessage, Does.Contain("7x7"));
    }

    [Test]
    public void Coverage_BelowMaxRadius_Fails_WithSuggestedMaxRadius()
    {
        var options = MakeOptions(cellSize: 50f, scanCellRadius: 1, maxRadius: 150f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.FailureMessage, Does.Contain("<= 50"));
    }

    [Test]
    public void CellSize_Zero_Fails()
    {
        var options = MakeOptions(cellSize: 0f, scanCellRadius: 1, maxRadius: 100f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.FailureMessage, Does.Contain("CellSize"));
    }

    [Test]
    public void CellSize_Negative_Fails()
    {
        var options = MakeOptions(cellSize: -1f, scanCellRadius: 1, maxRadius: 100f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.FailureMessage, Does.Contain("CellSize"));
    }

    [Test]
    public void ScanCellRadius_Negative_Fails()
    {
        var options = MakeOptions(cellSize: 50f, scanCellRadius: -1, maxRadius: 100f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.FailureMessage, Does.Contain("ScanCellRadius"));
    }

    [Test]
    public void MaxRadius_Negative_Fails()
    {
        var options = MakeOptions(cellSize: 50f, scanCellRadius: 1, maxRadius: -1f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.FailureMessage, Does.Contain("MaxRadius"));
    }

    [Test]
    public void MultipleFailures_AreAllReported()
    {
        var options = MakeOptions(cellSize: -1f, scanCellRadius: -1, maxRadius: -1f);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures!.Count, Is.EqualTo(3));
    }

    private static SpatialHashAreaOfInterestOptions MakeOptions(float cellSize, int scanCellRadius, float maxRadius) =>
        new()
        {
            Tier0Radius = 20f,
            Tier1Radius = 50f,
            MaxRadius = maxRadius,
            CellSize = cellSize,
            ScanCellRadius = scanCellRadius,
        };
}
