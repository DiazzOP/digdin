using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.App.UI;

/// <param name="Role">"Conta de origem (saques AstraFlip)", "Destinatária" or "Fora da distribuição".</param>
/// <param name="CopiesText">One line per copy (most valuable first), already formatted.</param>
internal sealed record LimitedsDetail(string Role, SpaceView? Space, int DaysToSale, string CopiesText, string ReadText, bool CanRead, bool Reading);
