# Network Debug Skill
- Keep MTU, DNS, NIC capabilities and adapter restart independent.
- Never change MTU without measurement; preserve verified 1492 unless evidence changes.
- Do not force unsupported RSS/RSC/NIC properties.
- Prefer .NET/IP Helper/registry read-only paths before PowerShell/WMI.
- Every apply requires timeout, cancellation and read-back.
- DNS benchmark must be bounded/concurrent and never block UI.
