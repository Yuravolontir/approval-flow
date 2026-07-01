namespace ApprovalFlow.Shared.Constants;

public static class PolicyRules
{
    // Meal rules
    public const string Meal01_Attendees = "MEAL-01";
    public const string Meal02_ClientEntertainment = "MEAL-02";
    public const string Meal03_AlcoholOnly = "MEAL-03";

    // Travel rules
    public const string Travel01_Eligible = "TRAVEL-01";
    public const string Travel02_Over1500 = "TRAVEL-02";
    public const string Travel03_FirstBusiness = "TRAVEL-03";

    // SaaS rules
    public const string Saas01_MonthlyCap = "SAAS-01";

    // Hardware rules
    public const string Hw01_Eligible = "HW-01";
    public const string Hw02_Capital = "HW-02";

    // Global rules
    public const string GlobalReceipt = "GLOBAL-RECEIPT";
    public const string GlobalVendor = "GLOBAL-VENDOR";
    public const string GlobalFx = "GLOBAL-FX";
    public const string GlobalDup = "GLOBAL-DUP";
    public const string GlobalMath = "GLOBAL-MATH";
    public const string GlobalFraud = "GLOBAL-FRAUD";

    // Autonomy
    public const string AutonomyCeiling = "AUTONOMY-CEILING";
    public const string AutonomyConfidence = "AUTONOMY-CONFIDENCE";
}

public static class Categories
{
    public const string Meals = "meals";
    public const string Travel = "travel";
    public const string Saas = "saas";
    public const string Hardware = "hardware";
    public const string Other = "other";
}

public static class DaprComponents
{
    public const string StateStore = "statestore";
    public const string PubSub = "pubsub";
    public const string SecretStore = "localsecretstore";

    public const string InvoiceSubmittedTopic = "invoice.submitted";
    public const string InvoiceProcessedTopic = "invoice.processed";
}
