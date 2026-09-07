namespace SmartX.Core.Devices;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);
}

/// <summary>
/// Recursively walks a Smart-X deployment tree (Facility -> Zone -> Sub-Zone
/// -> Device) and validates every Device node: that it carries a sensor
/// profile, that its MAC address is unique across the whole tree, and that
/// every ancestor above it (Sub-Zone, Zone, Facility) is itself named and
/// configured; a device cannot be considered "safely configured" if any
/// node in its Sub-Zone B -> Zone 1 -> Facility A chain is incomplete.
/// </summary>
public static class DeploymentTreeValidator
{
    public static ValidationResult Validate(DeploymentNode root)
    {
        var seenMacAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        ValidateRecursive(root, ancestry: [], seenMacAddresses, errors);

        return errors.Count == 0 ? ValidationResult.Success : new ValidationResult(false, errors);
    }

    private static void ValidateRecursive(
        DeploymentNode node,
        List<DeploymentNode> ancestry,
        HashSet<string> seenMacAddresses,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
        {
            errors.Add($"Node '{node.Id}' has no name.");
        }

        // Base case of the recursion is implicit: a node with no Children
        // simply skips the loop below and returns without recursing further.
        var pathIncludingSelf = new List<DeploymentNode>(ancestry) { node };

        if (node.NodeType == DeploymentNodeType.Device)
        {
            ValidateDevice(node, pathIncludingSelf, seenMacAddresses, errors);
        }

        foreach (var child in node.Children)
        {
            ValidateRecursive(child, pathIncludingSelf, seenMacAddresses, errors);
        }
    }

    private static void ValidateDevice(
        DeploymentNode device,
        List<DeploymentNode> pathIncludingSelf,
        HashSet<string> seenMacAddresses,
        List<string> errors)
    {
        if (device.Sensor is null)
        {
            errors.Add($"Device '{device.Id}' has no sensor profile attached.");
            return;
        }

        if (string.IsNullOrWhiteSpace(device.Sensor.MacAddress))
        {
            errors.Add($"Device '{device.Id}' is missing a MAC address.");
        }
        else if (!seenMacAddresses.Add(device.Sensor.MacAddress))
        {
            errors.Add($"Duplicate MAC address '{device.Sensor.MacAddress}' detected at '{DescribePath(pathIncludingSelf)}'.");
        }

        var ancestors = pathIncludingSelf.Take(pathIncludingSelf.Count - 1);
        foreach (var ancestor in ancestors)
        {
            if (string.IsNullOrWhiteSpace(ancestor.Name))
            {
                errors.Add($"Device '{device.Id}' cannot be validated: ancestor '{ancestor.Id}' is not configured.");
            }
        }
    }

    private static string DescribePath(List<DeploymentNode> path) =>
        string.Join(" -> ", path.Select(n => n.Name));
}
