# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

nc-bank-money = {$amount} €$
nc-bank-percent = {$percent}%
nc-bank-program-name = Bank
nc-bank-cash-slot-name = Cash

nc-bank-spawn-account = Night City Bank: your account is {$number}, balance {$balance} €$.
nc-bank-salary-paid = Payday: +{$amount} €$. Balance {$balance} €$.
nc-bank-salary-missed = Payday: your department could not pay your salary.

nc-bank-no-cash = Put cash in the slot first.
nc-bank-invalid-amount = Invalid amount.
nc-bank-insufficient-funds = Not enough money on the account.
nc-bank-deposited = Deposited {$amount} €$ (bank fee {$fee} €$).
nc-bank-withdrawn = Withdrew {$amount} €$.
nc-bank-transfer-success = Transfer sent.
nc-bank-account-not-found = No such account.
nc-bank-same-account = You cannot transfer to the same account.

nc-bank-no-account = No bank account is linked to you.
nc-bank-offline = The bank is offline.
nc-bank-account-label = Account:
nc-bank-balance-label = Balance:
nc-bank-fee-label = Bank fee:
nc-bank-cash-label = Cash in slot:
nc-bank-cash-hint = Insert cash into the slot, then deposit it.
nc-bank-deposit = Deposit cash
nc-bank-withdraw = Withdraw
nc-bank-amount-placeholder = Amount
nc-bank-target-placeholder = Account number (NC-000000)
nc-bank-transfer-title = Transfer
nc-bank-transfer = Send
nc-bank-transfer-total = You pay {$total} €$ including {$fee} €$ fee.
nc-bank-history-title = This round
nc-bank-history-empty = No operations this round.
nc-bank-history-line = {$time}  {$kind}  {$amount} €$
nc-bank-history-fee = (fee {$fee})
nc-bank-history-counterparty = ↔ {$counterparty}
nc-bank-history-note = · {$note}

nc-bank-kind-opening = Account opened
nc-bank-kind-deposit = Cash deposit
nc-bank-kind-withdrawal = Cash withdrawal
nc-bank-kind-transfer-out = Transfer sent
nc-bank-kind-transfer-in = Transfer received
nc-bank-kind-salary = Salary
nc-bank-kind-admin-grant = Budget grant
nc-bank-kind-admin-withdrawal = Budget withdrawal
nc-bank-kind-salary-missed = Salary not paid
nc-bank-kind-contract = Contract reward
nc-bank-kind-fixer = Fixer payment
nc-bank-kind-rent = Rent
nc-bank-kind-purchase = Purchase
nc-bank-kind-service-payment = Service payment
nc-bank-kind-service-refund = Service refund
nc-bank-kind-unknown = Operation

nc-bank-atm-title = Night City Bank ATM
nc-bank-terminal-title = Department finances
nc-bank-terminal-denied = Only the department manager can use this terminal.
nc-bank-salaries-title = Salaries per payday
nc-bank-salary-apply = Set

nc-bank-admin-title = Night City Bank (admin)
nc-bank-admin-departments = Department accounts
nc-bank-admin-adjust-title = Grant or withdraw
nc-bank-admin-lookup = Find
nc-bank-admin-select = Select
nc-bank-admin-amount-placeholder = Amount
nc-bank-admin-reason-placeholder = Reason (required)
nc-bank-admin-grant = Grant
nc-bank-admin-take = Withdraw
nc-bank-admin-fees-title = Bank fees
nc-bank-admin-fee-resident = Residents (personal accounts)
nc-bank-admin-owner-character = character
nc-bank-admin-lookup-result = {$number} ({$owner}): {$balance} €$
nc-bank-admin-reason-required = Enter a reason.
nc-bank-admin-adjust-failed = Not done: the account does not exist or has too little money.
nc-bank-admin-adjusted = {$number} changed by {$amount} €$.
nc-bank-admin-fee-set = Fee rate saved.

cmd-ncbank-desc = Opens the Night City Bank admin window.
cmd-ncbank-help = Usage: ncbank

ent-NCATM = ATM
    .desc = Night City Bank terminal. Deposit and withdraw cash on your own account.
ent-NCBankTerminalBase = department finance terminal
    .desc = Department account terminal. Only the department manager can use it.
ent-NCBankCartridge = bank cartridge
    .desc = Night City Bank program: balance, history and transfers.

nc-bank-account-militech-hq = Militech (corporation)
nc-bank-account-biotechnica-hq = Biotechnica (corporation)
nc-bank-account-vance-shell = Vance & Partners Consulting
