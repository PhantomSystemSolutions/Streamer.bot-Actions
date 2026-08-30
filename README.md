Current Streamer.bot Version: 1.0.7
# Streamer.bot-Actions

1. System boundaries
  - Inventory System
    - Owns:
      - User inventory files
      - Item quantities
      - User identity stored in inventory
      - Inventory username migration
      - Inventory persistence
    - Exposes:
    
      | Operation | Purpose |
      |----------|----------|
      | Get | Get quantity of one item |
      | Has | Check whether user has at least a specified quantity |
      | Add | Add items |
      | Remove | Remove items |
      | Set | Set an item's quantity |

The Shop and Currency systems should never call SaveInventory() or manipulate the Inventory JSON directly.

  - Currency System
    - Owns:
      - User currency balances
      - Currency definitions
      - Currency persistence
      - Currency transactions
    - Exposes:
      | Operation | Purpose |
      |----------|----------|
      | Get | Get balance |
      | Has | Check whether user has enough |
      | Add | Give currency |
      | Remove | Spend currency |
      | Set | Set balance |

Currency does not know what a Shop item is.

  - Shop System
    - Owns:
      - Shop item definitions
      - Item availability
      - Item display information
      - Item price
      - Currency required to purchase an item
      - Shop configuration
    - Exposes:
      | Operation | Purpose |
      |----------|----------|
      | Get | Get information about an item |
      | List | Get available shop items |
      | IsAvailable | Determine whether an item can currently be purchased |
      | Purchase | Coordinate a purchase |

The Shop should not own the user's inventory or currency balance.
