# Getting SpreadTrader onto Azure - beginner walkthrough

This is the complete path from "nothing installed" to "live on the internet, auto-deploying on
every push." Do it in order - each phase depends on the one before it. It'll take a while the
first time; that's normal.

If anything on screen doesn't match what's described here (buttons move, wording changes), that's
fine - tell me what you see and I'll adjust.

---

## Phase A - Install two tools

You need two programs on your computer. Neither requires Docker to be installed - the images get
built in the cloud (`az acr build`), not on your machine.

### A1. Git for Windows

1. Go to https://git-scm.com/download/win - the download starts automatically.
2. Run the installer. Click "Next" through every screen, keeping the default option selected each
   time. Click "Install" at the end.
3. Once it's done, find **"Git Bash"** in your Start menu and open it. A black/dark terminal
   window opens - this is what you'll run every command in for the rest of this guide.
4. Type this and press Enter, to confirm it worked:
   ```
   git --version
   ```
   You should see something like `git version 2.4x.x`. If you see an error, the install didn't
   finish - re-run the installer.

### A2. Azure CLI

1. In the same Git Bash window, run:
   ```
   winget install -e --id Microsoft.AzureCLI
   ```
   (If `winget` isn't recognized, download the installer directly instead: search "Azure CLI
   Windows install" or go to https://aka.ms/installazurecliwindows, run the downloaded `.msi`.)
2. **Close and reopen Git Bash** after installing (so it picks up the new program).
3. Confirm it worked:
   ```
   az --version
   ```
   You should see a version number, not an error.

---

## Phase B - GitHub account and repository

GitHub is where your code lives online; it's also what GitHub Actions (the auto-deploy robot)
reads from.

### B1. Create a GitHub account

If you don't already have one: go to https://github.com/signup and follow the prompts (email,
password, username). Free is fine.

### B2. Create an empty repository

1. Once logged in, go to https://github.com/new
2. **Repository name**: anything, e.g. `spreadtrader`
3. Leave it set to **Private** (recommended - this is a trading app) or Public, your call.
4. **Do not** check "Add a README" or any other initialize option - leave everything else
   unchecked/empty.
5. Click **Create repository**. You'll land on a page with setup instructions - keep this page
   open, you need the URL it shows you in a minute.

### B3. Push your code to it

In Git Bash, navigate to your project folder (adjust the path if yours differs):

```
cd "/d/LiveSpreadCalculator/LiveSpreadCalculator"
```

(Git Bash uses `/d/...` instead of `D:\...` - forward slashes, lowercase drive letter.)

Then run these one at a time:

```
git init
git add .
git commit -m "Initial commit"
git branch -M main
```

Now copy the remote URL GitHub showed you in step B2 (it looks like
`https://github.com/<your-username>/spreadtrader.git`) and run:

```
git remote add origin https://github.com/<your-username>/spreadtrader.git
git push -u origin main
```

This will ask you to sign in to GitHub - a browser window should pop up, log in there and come
back to the terminal. If it asks for a "personal access token" instead of a password, see
https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/creating-a-personal-access-token
(pick "repo" scope) and paste the token where it asks for a password.

Refresh the GitHub page in your browser - you should now see all your project files there.

---

## Phase C - Azure account

### C1. Sign up

If you don't already have an Azure account: go to https://azure.microsoft.com/free and sign up.
You'll need a phone number and a card for identity verification (you won't be charged unless you
explicitly upgrade past the free credits/tier - the resources this guide creates are small and
cheap, roughly a few dollars a month for the `B1` App Service plan).

### C2. Log in from the terminal

In Git Bash:

```
az login
```

A browser window opens - sign in with the same Microsoft/Azure account. Once it says
"you have logged in", close that browser tab and go back to Git Bash.

Confirm you're on the right subscription:

```
az account show
```

If you have more than one subscription and need to pick a specific one, tell me and I'll give you
the exact command.

---

## Phase D - Run the setup script

This creates everything in Azure: the resource group, container registry, storage, and the
running app.

In Git Bash, from the project folder:

```
cd "/d/LiveSpreadCalculator/LiveSpreadCalculator"
bash scripts/azure-setup.sh
```

It will:
- Print the names it's about to create and ask you to press Enter to confirm.
- Take several minutes (building images in the cloud, creating resources) - this is normal, let
  it run.
- Near the end, ask for your **Kite Connect API Key and API Secret** - paste them when prompted
  (the secret won't show on screen as you paste it, that's intentional).
- At the very end, print a URL (`https://spreadtrader-appXXXXX.azurewebsites.net`) and three
  values labeled `AZURE_RESOURCE_GROUP`, `AZURE_WEBAPP_NAME`, `ACR_NAME`.

**Copy those three values somewhere** (a notes app, whatever) - you need them in Phase F.

If a command in the middle fails with an error, stop and paste me the error message - don't keep
going past a failure.

Once it finishes, open the URL it printed in your browser. You should see the dashboard.

### D1. One manual step: Kite redirect URL

The script also prints a line like:
```
Register this redirect URL in the Kite Connect developer console for your API key:
  https://spreadtrader-appXXXXX.azurewebsites.net/api/auth/callback
```
Go to https://developers.kite.trade/apps, open your app, and paste that URL into the redirect URL
field, then save. Without this, clicking "Login to Kite" on the dashboard will fail.

---

## Phase E - Verify it's actually working

Open the dashboard URL. Check:
- The page loads and shows the sidebar/nav (not a blank page or an error).
- Top bar shows "Live feed: connected" (may take a few seconds).
- Click "Login to Kite" - it should redirect to Kite's login page, and after logging in, bounce
  you back to the dashboard with "Kite: authenticated".

If any of those don't work, tell me exactly what you see and I'll help debug it - it's almost
always one specific thing (wrong app setting, redirect URL not registered yet, etc.), not
something wrong with the whole setup.

---

## Phase F - GitHub Actions (auto-deploy on every push)

This makes it so that next time you (or I) change code and push to GitHub, Azure updates
automatically - no more running the script by hand.

### F1. Create the deploy credentials

You need your subscription ID first:

```
az account show --query id -o tsv
```

Copy that value, then run (replace `<subscription-id>` and `<your-RG>` - the resource group name
from Phase D's output):

```
az ad sp create-for-rbac --name spreadtrader-deploy \
  --role contributor \
  --scopes /subscriptions/<subscription-id>/resourceGroups/<your-RG> \
  --sdk-auth
```

This prints a block of text starting with `{` and ending with `}` - something like:
```json
{
  "clientId": "...",
  "clientSecret": "...",
  "subscriptionId": "...",
  "tenantId": "..."
}
```
Select and copy the **entire block, including both curly braces**.

### F2. Add secrets on GitHub

Go to your repository on github.com, then:

1. Click **Settings** (top menu of the repo page, not your account settings).
2. In the left sidebar, click **Secrets and variables** → **Actions**.
3. Click the green **New repository secret** button.
4. Add these four, one at a time (click "New repository secret" again for each):

   | Name (type exactly) | Value |
   |---|---|
   | `AZURE_CREDENTIALS` | the whole `{ ... }` block from F1 |
   | `AZURE_RESOURCE_GROUP` | the resource group name from Phase D's output |
   | `AZURE_WEBAPP_NAME` | the web app name from Phase D's output |
   | `ACR_NAME` | the ACR name from Phase D's output |

### F3. Test it

Make any small change to a file, then in Git Bash:

```
git add .
git commit -m "Test deploy"
git push
```

Go to your GitHub repo → the **Actions** tab (top menu). You should see a workflow run start
within a few seconds. Click into it to watch the steps run live. If it goes green, your app just
redeployed automatically. If it goes red, click the failed step to see the error and send it to
me.

---

## Where things live, for reference

- `DEPLOYMENT.md` - the same steps, written for someone comfortable with a terminal already.
- `scripts/azure-setup.sh` - the script Phase D runs.
- `.github/workflows/deploy-azure.yml` - the auto-deploy workflow Phase F wires up.
