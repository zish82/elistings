# eListings – User Guide

eListings takes products from your suppliers (DropShipMall, Eden Horticulture, Safelincs, …), turns them into
marketplace listings, and publishes them to eBay. This guide walks through every page, step by step.

> Prices: supplier prices are shown as the supplier gives them (DropShipMall and Eden are **ex VAT**).
> The app adds VAT, delivery and your margin using the settings on **Suppliers & Delivery**.

---

## Contents

1. [Typical workflow](#1-typical-workflow)
2. [Signing in & first-time setup](#2-signing-in--first-time-setup)
3. [Dashboard – My Listings (`/`)](#3-dashboard--my-listings-)
4. [Products (`/products`)](#4-products-products)
5. [Product details (`/products/{id}`)](#5-product-details-productsid)
6. [Create / Edit Listing (`/create-listing`, `/edit-listing/{id}`)](#6-create--edit-listing)
7. [Bulk Extract (`/bulk-extract`)](#7-bulk-extract-bulk-extract)
8. [Suppliers & Delivery (`/suppliers`)](#8-suppliers--delivery-suppliers)
9. [Supplier Feeds (`/supplier-feeds`)](#9-supplier-feeds-supplier-feeds)
10. [eBay Accounts (`/ebay-accounts`)](#10-ebay-accounts-ebay-accounts)
11. [Users (`/users`)](#11-users-users)
12. [Writing good titles](#12-writing-good-titles)
13. [Background jobs](#13-background-jobs)
14. [Troubleshooting](#14-troubleshooting)

---

## 1. Typical workflow

1. **Connect eBay** once on *eBay Accounts* (and create business policies in eBay Seller Hub).
2. **Check supplier settings** on *Suppliers & Delivery* (VAT, margin, delivery prices, SKU prefix).
3. **Import products** on *Products* (choose the supplier → **Import …**).
4. **Find products** using sort/filters (e.g. *Supplier price: high to low*, *In stock only*).
5. **Add to my listings** – drafts are created with price, delivery, images, SKU and item specifics filled in.
6. **Review each draft** on *Edit Listing*: title, price, category, item specifics, policies.
7. **Save & Publish** (one listing) or select several on the Dashboard → **Bulk Publish to eBay**.

---

## 2. Signing in & first-time setup

### Sign in (`/login`)
1. Enter your **Email** and **Password**.
2. Click **Sign In**. You are taken to the Dashboard.

### Create the first admin (`/setup-admin`) – only once, on a new installation
1. Enter the admin **Email** and **Password**.
2. Click **Create First Admin**, then sign in.

Roles:
- **Admin** – everything, including managing users and supplier settings.
- **Manager** – sees everyone's listings, can edit supplier settings.
- **Lister** – works on their own listings.

---

## 3. Dashboard – My Listings (`/`)

Shows all your listings (drafts and published).

| Column | Meaning |
|---|---|
| Price | Listing total (item price + delivery). Small text shows the split. |
| Source Price / Source Stock | Supplier's price and stock for the product |
| eBay Account | Which eBay account the listing will use |
| Status | **Draft** or **Published** |
| eBay Item ID | Set once published |

**Buttons at the top:** Create New Listing, Bulk Extract Category, Manage eBay Accounts, Supplier Feeds, Suppliers & Delivery, Products.

### Publish one listing
1. Click **Publish** (or **Re-sync** for a published listing) on its row.
2. A message at the top tells you whether it worked, and why not if it failed.

### Publish many listings
1. Tick the listings (or the header checkbox to select all shown).
2. Click **Bulk Publish to eBay**. A progress bar shows how many succeeded/failed, with the reason for each failure.
3. You can keep working while it runs.

Before publishing, each listing is checked for: a title (max 80 characters), a price, at least one image and
payment/shipping/return policies. If no eBay category is set, the app picks eBay's best suggestion from the title.

### Delete
Tick listings → **Bulk Delete**. Drafts are removed; published listings are marked deleted.

---

## 4. Products (`/products`)

Supplier products you can turn into listings.

### Import / refresh a supplier's products
1. Choose the **Supplier** at the top.
2. Click **Import …** under *Available feeds* (e.g. *Import Product Catalogue* for DropShipMall,
   *Import Shopify Catalogue* for Eden).
3. Long imports (Eden) run in the background with a progress bar; the button shows **Importing…** until it
   finishes. You can leave the page – when you come back the progress bar reappears while it is still running.
4. *Last updated* shows when the supplier's products were last refreshed.

Re-running an import is safe: existing products are updated, never duplicated. Only one import per supplier
runs at a time; clicking again while one is running just shows its progress.

Imports also run automatically every night. Notes:
- **DropShipMall** – discontinued products are skipped/hidden.
- **Eden** – each size/variant is its own row (e.g. "Root Riot - 100 Bag"). Trade prices and exact stock come
  from your logged-in Eden account. A yellow **est.** badge means the trade price couldn't be confirmed yet
  (RRP shown instead). Products Eden removes from their website are hidden automatically.
- **Safelincs** – built from Safelincs' website (every product page, one at a time, so a full import takes a few
  hours and runs overnight). Each variant in a page's dropdown is its own row. Trade prices and per-product
  delivery come from the **trade price list** you upload on *Supplier Feeds*; until then the website price
  (ex VAT) is used and marked **est.** "Available to Order" items have no stock figure (shown as *-*);
  discontinued items are hidden.
- Products you **delete** here stay deleted, even after re-importing.
- Products of a **disabled** supplier (see *Supplier Feeds*) are hidden, and the supplier isn't in the list.

### Categories view
Click **Categories** (above the filters) to see the supplier's own categories with the number of products,
how many are in stock and how many you've already added. Click a category to open its products in the list
(the *Category* filter is set for you; *(No category)* shows products without one). Click **Products** to go back.

### Find products
The filter bar above the list:
1. **Sort by** – *Supplier price: high to low* (default), *Approx. profit*, *Stock*, *Title*.
   You can also click the **Product / Stock / Supplier Price / Approx. Profit** column headers.
2. **Category**, **Min/Max price**, **In stock only**, **Hide already added**.
3. **Search** (top) – title or SKU, press **Enter**.
4. Click **Apply filters** (sort and category apply immediately). **Clear** resets everything.
5. Up to 500 products are shown at once – narrow the filters to see others.

**Approx. Profit** = suggested price − supplier price. It's a rough guide for spotting valuable items: it does
**not** deduct VAT, delivery or eBay fees.

### Add products to your listings
- **Add to my listings** on a row, or tick several and click **Add all selected to my listings**.
- Each new draft gets: price (cost + VAT + your margin), the default delivery option, all images, SKU,
  brand, item specifics (including variant options like *Size*), and the supplier's original title.
- For DropShipMall products, descriptions and specifications are then fetched from the supplier website in the
  background (progress bar shown).
- **Add duplicate** creates a second listing of the same product (e.g. a different pack size or price test).
- **Open listing** jumps to the listing already made from that product.

---

## 5. Product details (`/products/{id}`)

Click a product title on *Products* to see its full details (description, images, prices).
Use **Add to my listings** (or add a duplicate) from here too.

---

## 6. Create / Edit Listing

`/create-listing` for a new listing, `/edit-listing/{id}` to edit.

### Top buttons (listings made from a supplier product)
- **Fetch Missing Details** – fetches description, specifications and images from the supplier website and fills
  any **empty** fields (your edits are kept).
- **Reset from Source Product** – overwrites title, description, source fields and main image from the product.

### Quick Fill from URL (new listings)
1. Paste a supplier product URL (Safelincs, Eden…).
2. Click **Extract Details**. Title, description, images, brand and product code are filled in.
   If it fails, the message says why (e.g. the site blocked the request).

### Title
- Live counter **x/80** (eBay's limit). Green from 65 characters, red over 80.
- The formula reminder is shown under the field (see [Writing good titles](#12-writing-good-titles)).
- *Original supplier title* is shown underneath when you've changed it.
- **✨ Suggest Category** suggests eBay categories from the title – click one to use it.

### Price & Delivery
| Field | Meaning |
|---|---|
| Item Price (inc VAT) | What you charge for the item. *Use suggested* fills cost + VAT + margin. |
| Delivery Option | Supplier delivery charge; the product's matching option is pre-selected. |
| Delivery Cost (inc VAT) | Editable. |
| Listing Total | Item price + delivery – the price sent to eBay. |

Delivery is **always included** in the listing price for now (listed with free postage); separate postage
charges aren't supported yet. The line underneath shows your cost (item + VAT + delivery) and the margin
before eBay fees.

### Other fields
- **eBay Account** – which connected account to list on.
- **SKU** – leave blank to generate `{supplier prefix}-{supplier code}`, e.g. `DSM-S16301`. Can't be changed
  after publishing.
- **Quantity** – stock to offer on eBay.
- **eBay Category ID / Name** – set by *Suggest Category* or typed in.
- **Brand, Type, Colour, Barcode** – item specifics sent to eBay.
- **Source Price / Source Product Code** – supplier price and code (used for cost and margin).
- **Check Current Supplier Stock** – live stock (and trade price for Eden) from the supplier.

### eBay Item Specifics for this category
Appears once a category and eBay account are set. Required fields have a red **\***.
- Pick from the dropdown, or choose **Other (type your own)…** where eBay allows custom values.
- *Recommended* fields are optional but help buyers find the item.
- **Save & Publish** warns you if required specifics are empty.

### Images & Description
- Remove images with **×**, add by URL, or upload JPG/PNG files (max 5 MB each, 10 at a time).
- The description editor supports bold, italic and lists.

### eBay Business Policies
Choose Payment, Fulfillment (shipping) and Return policies. If none appear, a message explains why
(e.g. the account isn't opted in to business policies – see [Troubleshooting](#14-troubleshooting)).

### Saving
- **Save as Draft / Save Changes** – saves without publishing.
- **Save & Publish / Save & Re-sync** – saves and publishes to eBay (status shown below the button).
- **Discard Changes** – back to the Dashboard without saving.

*Preview Fees* is not working yet (eBay doesn't provide a fee estimate before selling).

---

## 7. Bulk Extract (`/bulk-extract`)

Creates drafts for every product in a supplier category page.

1. Paste a category URL, e.g. `https://www.safelincs.co.uk/heat-alarms/` or
   `https://edenhorticulture.co.uk/collections/lighting-kits`.
2. Click **Bulk Extract**. A progress bar shows created / skipped (already exist) / failed.
3. Open the Dashboard to review the drafts. Products without a price are saved at £0.00 – set a price before
   publishing.

For Eden it's usually better to use **Products → Import Shopify Catalogue**, which brings trade prices and stock.

---

## 8. Suppliers & Delivery (`/suppliers`)

One card per supplier. Change values, then click **Save supplier**.

| Setting | Meaning |
|---|---|
| Website host | Matches listings created from that supplier's URLs |
| SKU prefix | Start of generated SKUs (e.g. `DSM`, `EDN`) |
| VAT rate % | Usually 20 |
| Default margin % | Used for suggested prices |
| Prices include VAT | Tick only if the supplier's prices already include VAT (DropShipMall and Eden: **unticked**) |

### Delivery options
1. **+ Add delivery option** (or edit an existing row).
2. Enter **Name**, **Cost**, tick **Inc VAT?** only if the cost already includes VAT.
3. **Postage categories** (optional): the supplier's postage classes this option applies to, comma separated
   (e.g. `Standard Parcel,Small Parcel`). Products in those classes get this option by default.
4. Choose one **Default** option (used when no postage category matches).
5. Click **Save** on the row. **×** deletes it.

**Add supplier** (bottom): Code (must match the supplier name used by its feed), Name, website host, SKU prefix.

---

## 9. Supplier Feeds (`/supplier-feeds`)

### Suppliers: enable / disable
The **Suppliers** table at the top (Admin/Manager) shows each supplier's products, listings and listings on eBay.
- Switch a supplier **off** to stop its imports and syncs and hide its products and listings in the app
  (the Dashboard has a *Show listings from disabled suppliers* tick box). Its listings can't be published while it's off.
- Its eBay listings are **not** changed. To stop sales, click **Set eBay quantity to 0** (runs in the background).
  If *out-of-stock control* is off in your eBay account, eBay may end listings at quantity 0.
- Switch it back **on** and click **Restore eBay quantity** to put those listings back to their own quantity.

### Trade price list (Safelincs)
Choose the Excel (.xlsx) or CSV price list in the *Trade price list* column. It needs a **Product Code** and a
**Price (ex VAT)** column (a **Delivery** column is used too). Uploading replaces the previous list and
re-prices imported products straight away; the nightly import uses it as well.

### Feeds
Feed URLs used by the importers (mainly DropShipMall).

1. Fill in **Supplier**, **Feed Type**, **URL**, optional **Description**.
2. **Cookie Header** is only needed for feeds that require a login when no username/password is configured.
3. Tick **Recommended feed** for the preferred one → **Add Feed**. Remove feeds with the delete button.

Supplier logins (DropShipMall, Eden) are configured by your administrator on the server, not on this page.

---

## 10. eBay Accounts (`/ebay-accounts`)

### Connect an account
1. Enter a **Name** for the account (e.g. "Main shop").
2. Click **Connect OAuth Account** and sign in to eBay, then accept.
3. You return here with the account listed. (Locally this needs the ngrok tunnel to be running.)

### Manage
- **Default** – used for new listings.
- **Refresh** – renews the access token (normally automatic).
- **Delete** – only when no listings use the account.
- **Save Manual Token** – paste a token from the eBay developer portal (expires after ~2 hours; for testing).

The eBay account must have **business policies** (Payment, Postage, Returns) set up in Seller Hub →
Account → Business policies, otherwise listings can't be published.

---

## 11. Users (`/users`) – admins only

1. **Add User**: Email, password, role (Admin / Manager / Lister) → **Add**.
2. In *Existing Users*, change role or tick/untick **Active**, then **Save**.

---

## 12. Writing good titles

**Brand + what it is + key attributes (size, colour, material, quantity) + use/occasion**

- Put the words buyers search for first ("pillar candle", "vase filler", "planter").
- Use 65–80 characters; skip symbols, brackets and filler words ("L@@K", "wow").
- Only true facts from the product data – never invent features.

| Supplier title | Better |
|---|---|
| 150x80mm Church Candle | Ivory Church Pillar Candle 15cm x 8cm 68 Hour Burn Time Chapel Candle |
| Plastic Black Hanging Basket (14 inch) | 14 Inch Black Plastic Hanging Basket Planter for Garden Flowers and Plants |

---

## 13. Background jobs

Imports, bulk extract, bulk publish and "fetch details" run in the background (Hangfire), so you can keep
working or even refresh the page – the progress bar picks up where it was.

- A job first shows **Queued – waiting to start**, then **Running**, then **Completed** or **Failed** with reasons.
- Jobs that contact **Eden Horticulture** run one at a time, and every request to Eden's website is spaced at
  least 2 seconds apart, so the app never floods their site.
- Jobs started from a page are **not retried automatically** – if one fails, fix the cause and start it again.
  Scheduled Eden jobs retry once, an hour later.
- Administrators can see every job (queued, running, succeeded, failed, schedules) at **`/hangfire`**
  (development mode), and trigger scheduled jobs from there.
- If the server restarts while a job is running, it shows *Interrupted by a server restart*; Hangfire normally
  resumes it automatically.
- Hangfire keeps its data in the app database (PostgreSQL schema `hangfire`). See **DEPLOY.md** for the database,
  Docker and hosting setup.

---

## 14. Troubleshooting

| Message / problem | What to do |
|---|---|
| *User is not eligible for Business Policy* | Opt in to business policies in eBay Seller Hub and create Payment, Postage and Returns policies. |
| *The item specific X is missing* | Fill **X** in *eBay Item Specifics* on the listing, then publish again. |
| *A system error has occurred* (eBay 25001) | Temporary eBay error – the app retries automatically; try again later if it persists. |
| *Cannot publish: price is not set / no images / title is N characters* | Fix the field named and publish again. |
| Import shows *est.* prices for Eden | The trade price couldn't be confirmed; check the Eden login is configured and re-run the import. |
| *Could not reach the server* | The server isn't running or the page was opened on the wrong address. |
| Nothing happens after clicking a button | Refresh the page (Cmd+Shift+R) to load the latest version of the app. |
