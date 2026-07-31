using Avalonia.Input;

namespace BohemiX.Modules.Forge.Input;

public enum ForgeInputAction
{
    ToggleRecipe,
    TogglePause,
    PumpBellows,
    ContextAction,
    RotateWorkpiece,
    Reheat,
    QuenchWater,
    QuenchOil,
    BeginGrinding,
    SkipGrinding,
    IncreaseGrinderSpeed,
    DecreaseGrinderSpeed
}

public static class ForgeKeyboardMap
{
    public static bool TryResolve(KeyEventArgs args, out ForgeInputAction action)
    {
        if (TryResolve(args.PhysicalKey, args.KeyModifiers, out action))
        {
            return true;
        }

        return TryResolve(args.Key, args.KeyModifiers, out action);
    }

    public static bool TryResolve(Key key, KeyModifiers modifiers, out ForgeInputAction action)
    {
        action = default;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return false;
        }

        action = key switch
        {
            Key.Tab => ForgeInputAction.ToggleRecipe,
            Key.Escape => ForgeInputAction.TogglePause,
            Key.Space => ForgeInputAction.PumpBellows,
            Key.Enter => ForgeInputAction.ContextAction,
            Key.Q => ForgeInputAction.RotateWorkpiece,
            Key.R => ForgeInputAction.Reheat,
            Key.D1 or Key.NumPad1 => ForgeInputAction.QuenchWater,
            Key.D2 or Key.NumPad2 => ForgeInputAction.QuenchOil,
            Key.G => ForgeInputAction.BeginGrinding,
            Key.K => ForgeInputAction.SkipGrinding,
            Key.W or Key.Up => ForgeInputAction.IncreaseGrinderSpeed,
            Key.S or Key.Down => ForgeInputAction.DecreaseGrinderSpeed,
            _ => default
        };
        return key is Key.Tab or Key.Escape or Key.Space or Key.Enter or Key.Q or Key.R or
            Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.G or Key.K or
            Key.W or Key.Up or Key.S or Key.Down;
    }

    public static bool TryResolve(PhysicalKey key, KeyModifiers modifiers, out ForgeInputAction action)
    {
        action = default;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return false;
        }

        action = key switch
        {
            PhysicalKey.Tab => ForgeInputAction.ToggleRecipe,
            PhysicalKey.Escape => ForgeInputAction.TogglePause,
            PhysicalKey.Space => ForgeInputAction.PumpBellows,
            PhysicalKey.Enter or PhysicalKey.NumPadEnter => ForgeInputAction.ContextAction,
            PhysicalKey.Q => ForgeInputAction.RotateWorkpiece,
            PhysicalKey.R => ForgeInputAction.Reheat,
            PhysicalKey.Digit1 or PhysicalKey.NumPad1 => ForgeInputAction.QuenchWater,
            PhysicalKey.Digit2 or PhysicalKey.NumPad2 => ForgeInputAction.QuenchOil,
            PhysicalKey.G => ForgeInputAction.BeginGrinding,
            PhysicalKey.K => ForgeInputAction.SkipGrinding,
            PhysicalKey.W or PhysicalKey.ArrowUp => ForgeInputAction.IncreaseGrinderSpeed,
            PhysicalKey.S or PhysicalKey.ArrowDown => ForgeInputAction.DecreaseGrinderSpeed,
            _ => default
        };
        return key is PhysicalKey.Tab or PhysicalKey.Escape or PhysicalKey.Space or
            PhysicalKey.Enter or PhysicalKey.NumPadEnter or PhysicalKey.Q or PhysicalKey.R or
            PhysicalKey.Digit1 or PhysicalKey.NumPad1 or PhysicalKey.Digit2 or PhysicalKey.NumPad2 or
            PhysicalKey.G or PhysicalKey.K or PhysicalKey.W or PhysicalKey.ArrowUp or
            PhysicalKey.S or PhysicalKey.ArrowDown;
    }
}
