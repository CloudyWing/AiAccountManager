window.accountNavigation = {
  scrollTo: (id, account) => {
    if (new URLSearchParams(window.location.search).get("account") === account) {
      document.getElementById(id)?.scrollIntoView({ block: "start" });
    }
  }
};
