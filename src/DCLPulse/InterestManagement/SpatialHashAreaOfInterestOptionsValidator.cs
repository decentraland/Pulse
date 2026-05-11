using Microsoft.Extensions.Options;

namespace Pulse.InterestManagement;

public sealed class SpatialHashAreaOfInterestOptionsValidator : IValidateOptions<SpatialHashAreaOfInterestOptions>
{
    public ValidateOptionsResult Validate(string? name, SpatialHashAreaOfInterestOptions o)
    {
        var errors = new List<string>();

        if (o.CellSize <= 0)
            errors.Add($"CellSize must be > 0 (actual: {o.CellSize}).");

        if (o.ScanCellRadius < 0)
            errors.Add($"ScanCellRadius must be >= 0 (actual: {o.ScanCellRadius}).");

        if (o.MaxRadius < 0)
            errors.Add($"MaxRadius must be >= 0 (actual: {o.MaxRadius}).");

        if (o.CellSize > 0 && o.ScanCellRadius >= 0 && o.MaxRadius >= 0)
        {
            float coverage = o.ScanCellRadius * o.CellSize;

            if (coverage < o.MaxRadius)
            {
                int requiredRadius = (int)MathF.Ceiling(o.MaxRadius / o.CellSize);
                errors.Add(
                    $"Coverage mismatch: ScanCellRadius ({o.ScanCellRadius}) * CellSize ({o.CellSize}) = {coverage} is less than MaxRadius ({o.MaxRadius}). " +
                    $"Resolve by one of: " +
                    $"raise ScanCellRadius to >= {requiredRadius} (scan becomes {(2 * requiredRadius) + 1}x{(2 * requiredRadius) + 1} cells); " +
                    $"or lower MaxRadius to <= {coverage}; " +
                    $"or raise CellSize to >= {MathF.Ceiling(o.MaxRadius / Math.Max(o.ScanCellRadius, 1))}.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
