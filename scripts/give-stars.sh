#!/usr/bin/env bash
set -euo pipefail

# Give Stars to User
# Usage: ./give-stars.sh <user_id> <stars_amount>

usage() {
    echo "Usage: $0 <user_id> <stars_amount>"
    echo "Example: $0 2010001 1000"
}

if [ "$#" -ne 2 ]; then
    usage
    exit 1
fi

USER_ID=$1
STARS=$2

if [[ ! "$USER_ID" =~ ^[0-9]+$ ]]; then
    echo "❌ Error: user_id must be a positive integer" >&2
    exit 1
fi

if [[ ! "$STARS" =~ ^[1-9][0-9]*$ ]]; then
    echo "❌ Error: stars_amount must be a positive integer" >&2
    exit 1
fi

if [ "${#STARS}" -gt 7 ] || [ "$STARS" -gt 1000000 ]; then
    echo "❌ Error: max 1000000 stars per grant" >&2
    exit 1
fi

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

echo "⭐ Giving $STARS stars to user $USER_ID..."

set +e
RESULT=$(compose exec -T mongodb mongosh tg --quiet --eval "
const userId = NumberLong('$USER_ID');
const delta = NumberLong('$STARS');

const users = db.getCollection('eventflow-userreadmodel');
if (users.countDocuments({ UserId: userId }) === 0) {
  print('ERROR:USER_NOT_FOUND');
  quit(2);
}

const balances = db.getCollection('star-balances');
const beforeDoc = balances.findOne({ UserId: userId });
const before = beforeDoc && beforeDoc.Balance != null ? NumberLong(beforeDoc.Balance.toString()) : NumberLong(0);
if (Number(before.toString()) + Number(delta.toString()) > 2000000000) { print('ERROR:TOO_MUCH'); quit(3); }

const afterDoc = balances.findOneAndUpdate(
  { UserId: userId },
  {
    \$setOnInsert: { UserId: userId },
    \$inc: { Balance: delta }
  },
  { upsert: true, returnDocument: 'after' }
);

const txId = new ObjectId().toString();
db.getCollection('star-transactions').insertOne({
  _id: txId,
  TransactionId: txId,
  UserId: userId,
  Amount: delta,
  Date: Math.floor(Date.now() / 1000),
  Gift: false,
  Refund: false,
  PeerUserId: null,
  PeerChannelId: null,
  Title: 'CrikGram',
  Description: 'Added by scripts/give-stars.sh',
  StargiftUpgrade: false,
  StargiftAuctionBid: false,
  Offer: false,
  StargiftSlug: null,
  PremiumGiftMonths: null,
  StarrefCommissionPermille: null,
  StarrefPeerUserId: null,
  StarrefPeerChannelId: null,
  StarrefAmount: null,
  Pending: false,
  Failed: false,
  Reaction: false,
  BusinessTransfer: false,
  StargiftResale: false,
  PostsSearch: false,
  StargiftPrepaidUpgrade: false,
  StargiftDropOriginalDetails: false,
  PhonegroupMessage: false,
  PaidMessages: null,
  MsgId: null,
  TransactionDate: null,
  TransactionUrl: null
});

print('BEFORE:' + before.toString());
print('AFTER:' + afterDoc.Balance.toString());
print('TX:' + txId);
" | tr -d '\r')
MONGO_STATUS=$?
set -e

if grep -q '^ERROR:USER_NOT_FOUND$' <<<"$RESULT"; then
    echo "❌ Error: User $USER_ID not found!" >&2
    exit 1
fi

if [ "$MONGO_STATUS" -ne 0 ]; then
    echo "❌ Error: mongosh failed with status $MONGO_STATUS" >&2
    if [ -n "$RESULT" ]; then
        echo "$RESULT" >&2
    fi
    exit "$MONGO_STATUS"
fi

CURRENT_BALANCE=$(awk -F: '/^BEFORE:/ {print $2}' <<<"$RESULT" | tail -1)
NEW_BALANCE=$(awk -F: '/^AFTER:/ {print $2}' <<<"$RESULT" | tail -1)
TX_ID=$(awk -F: '/^TX:/ {print $2}' <<<"$RESULT" | tail -1)

if [[ -z "${CURRENT_BALANCE:-}" || -z "${NEW_BALANCE:-}" || -z "${TX_ID:-}" ]]; then
    echo "❌ Error: unexpected mongosh output:" >&2
    echo "$RESULT" >&2
    exit 1
fi

echo "Current balance: $CURRENT_BALANCE stars"
echo "✅ Successfully gave $STARS stars to user $USER_ID!"
echo "New balance: $NEW_BALANCE stars"
echo "Transaction: $TX_ID"
