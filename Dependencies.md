# Streamer.bot Systems Dependency Map

## Legend

Dependency Types:

- Required = System will not function correctly without it.
- Optional = Enhanced functionality only.
- Data = Reads data produced by another system.
- Service = Calls functions/actions in another system.

---

# Currency System

System:
- [CMS] Core

Purpose:
- Currency storage
- Currency transactions
- User balances
- Revenue tracking

Dependencies:
- None

Used By:
- Shop System
- Inventory System
- Subathon System
- Codeword System

---

# Inventory Management System

System:
- [IMS] Core

Purpose:
- User inventory management
- Registry management
- Item ownership

Dependencies:
- Currency System (Required)

Dependency Type:
- Service

Used By:
- Shop System

---

# Shop Management System

System:
- [SMS] Core

Purpose:
- Item purchasing
- Shop management
- Revenue generation

Dependencies:
- Currency System (Required)
- Inventory System (Required)

Dependency Type:
- Service

Used By:
- None

---

# Subathon Management System

System:
- [SubMS] Core

Purpose:
- Track subathon events
- Extension calculations
- Time management

Dependencies:
- Currency System (Required)
- Real World Currency Converter (Required)

Dependency Type:
- Service

Used By:
- None

---

# Codeword System

System:
- CodewordSystem

Purpose:
- Word rotations
- Viewer participation rewards

Dependencies:
- Currency System (Required)

Dependency Type:
- Service

Used By:
- None

---

# Shoutout System

System:
- ShoutoutCode

Purpose:
- Twitch shoutouts
- Viewer recognition

Dependencies:
- None

Used By:
- None

---

# Converters

System:
- Real World Currency Converter

Purpose:
- Currency conversion

Dependencies:
- None

Used By:
- Subathon System
