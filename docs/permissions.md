# Permission matrix

Every tenant-owned row has `OrganizationId`. The API loads the caller's membership and ignores any organization id the client sends unless that membership exists. Missing membership returns 404. A member who is not allowed to perform an action returns 403.

| Action | Admin | Dispatcher | Technician | Requester |
| --- | --- | --- | --- | --- |
| Update organization and SLA | Yes | No | No | No |
| Members and roles | Yes | View | No | No |
| Audit history | Yes | No | No | No |
| Notification jobs and retry | Yes | No | No | No |
| Reports | Yes | Yes | No | No |
| All work orders | Yes | Yes | No | No |
| Create work order | Yes | Yes | No | Own requests |
| View work orders | All | All | Unassigned open, plus own assignments | Own only |
| Claim | No | No | Open and unassigned | No |
| Assign or unassign | Yes | Yes | No | No |
| Update details | Yes, unless closed or cancelled | Yes, unless closed or cancelled | No | Own work, only while open |
| Status changes | See workflow | See workflow | Assigned work only | Cancel own open work; close own resolved work |
| Comment and upload | Visible, non-cancelled work | Visible, non-cancelled work | Accessible work | Own work |
| Delete attachment | Yes | Yes | Own uploads | Own uploads |

There is no public registration and no endpoint that creates an organization and grants the caller admin. Admins add members to organizations that already exist. Seed data is development-only.

List results for technicians and requesters are filtered to what they can see. Reading a specific work order outside that set returns 403 when they are a member of the organization. A work order or organization they do not belong to returns 404.

Claim is special: a technician in the organization who loses the race, or who targets a work order that is no longer open and unassigned, receives 409. Cross-organization claim attempts return 404.
