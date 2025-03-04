using ASDA.Integration.Logging;
using CoreHelpers;
using CoreHelpers.CommonModels;
using CustomerRegistration.Interface;
using CustomerRegistration.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TransformationHelper.Interface;
using static CoreHelpers.Constants.CoreConstants;
using static CoreHelpers.ExceptionHandler;


namespace CustomerRegistration;
public class CustomerRegistrationService : ICustomerRegistration
{
    private readonly ITransformationHelper transformationHelper;
    private readonly ISingleProfileRegistration singleProfileMethods;
    private readonly ICRMRegistration crmMethods;


    public CustomerRegistrationService(ICRMRegistration _crmMethods, ISingleProfileRegistration _singleProfileMethods, ITransformationHelper _transformationHelper)
    {
        crmMethods = _crmMethods;
        singleProfileMethods = _singleProfileMethods;
        transformationHelper = _transformationHelper;
    }



    private static ResponseObject PrepareResponsePayload(string message, string status, string newCustomerId, string oldCustomerId, JObject cookies, int statusCode, string? location = null)
    {
        JObject responsePayload = new()
        {
            ["message"] = message,
            ["accountStatus"] = status,
            ["oldCustomerID"] = string.IsNullOrEmpty(oldCustomerId)?"-":oldCustomerId,
            ["newCustomerID"] = newCustomerId,
            ["cookies"] = cookies
        };
        if (!string.IsNullOrEmpty(location))
        {
            responsePayload["location"] = location;
        }
        return new()
        {
            ApiStatusCode = statusCode,
            CustomResponsePayload = responsePayload
        };

    }

    private async Task<SingleProfileSegmentResponse> SingleProfileSegment(RegistrationRequestPayload registerRequestPayload, bool isThirdParty, string? userType, (string lastname, string title) spDetails)
    {


        if (TargetStatus.IsWmRequired)
        {
            JObject createUserSingleProfileResponse = await singleProfileMethods.CreateUserInSingleProfile(registerRequestPayload, isThirdParty, registerRequestPayload.BrandName,userType!,(spDetails.lastname,spDetails.title));

            // If Single Profle Create user fails - Return
            if (createUserSingleProfileResponse["status"]?.ToString() != SUCCESS_STATUS)
            {
                Logger.LogInfo($"Registration Failed at Single Profile");
                return new SingleProfileSegmentResponse("", "", new CookieResponse(), false, createUserSingleProfileResponse.SelectToken("data.errorDescription")!.ToString(), false, (new JObject(), new JObject()));

            }
            // User created in Single Profile. Extract customer id and tokens
            string oldCustomerID = createUserSingleProfileResponse.SelectToken("data.profileId")!.ToString();
            string singleProfileToken = createUserSingleProfileResponse.SelectToken("cookies.WM_SEC_AUTH_TOKEN")!.ToString();
            string cookiesInAString = (createUserSingleProfileResponse["cookies"] ?? "").ToString();
            CookieResponse cookies = JsonConvert.DeserializeObject<CookieResponse>(cookiesInAString) ?? new CookieResponse();
            // Since user is created in Single Profile, rollback will be required.
            // add only if not guest scenario
            bool rollbackRequired = userType != "Guest";

            try
            {
                // Call Single Profile to fetch user details.
                JObject userDetails = await singleProfileMethods.GetProfileDetailsFromSingleProfile(singleProfileToken, registerRequestPayload.BrandName);


                // If Single Profile Get Profile Fails - Rollback and Return
                if (userDetails["status"]?.ToString() == FAILED_STATUS && rollbackRequired)
                {
                    Logger.LogInfo("Registration Failed at Single Profile Get Profile, Rolling back");
                    _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName);
                    return new SingleProfileSegmentResponse(singleProfileToken, oldCustomerID, cookies, rollbackRequired, INTERNAL_SERVER_ERROR_MSG, false, (new JObject(), new JObject()));
                }
                // create dummy address for user coming from gi in Single Profile
                if (registerRequestPayload.BrandName == "gi")
                    _ = singleProfileMethods.CreateSingleProfileAddress(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName, registerRequestPayload.PostCode);

                return new SingleProfileSegmentResponse(singleProfileToken, oldCustomerID, cookies, rollbackRequired, "", true, (createUserSingleProfileResponse, userDetails));
            }
            catch (Exception ex)
            {
                LogHandler("AZFU", ex, new LogTemplate
                {
                    message = $"Registration Exception Failure {ex.Message} - {ex.StackTrace}",
                });
                if (rollbackRequired)
                {
                    Logger.LogInfo("Registration Failed at Single Profile Get Profile, Rolling back");
                    _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName);
                }
                return new SingleProfileSegmentResponse(singleProfileToken, oldCustomerID, cookies, rollbackRequired, INTERNAL_SERVER_ERROR_MSG, false, (new JObject(), new JObject()));
            }

        }

        CookieResponse cookieResponse = new CookieResponse();
        cookieResponse.SingleProfileLatCookie = "-";
        cookieResponse.SingleProfileUserToken = "-";
        cookieResponse.SingleProfileLtCookie = "-";

        return new SingleProfileSegmentResponse("", "", cookieResponse, false, "", true, (new JObject(), new JObject()));

    }



    public async Task<ResponseObject> CustomerRegistration(RegistrationRequestPayload registerRequestPayload, string CrmApiToken, string functionRootPath)
    {
        Logger.LogInfo("Registration Processing Started");
        JObject createUserSingleProfileResponse = new();
        string singleProfileToken = "";
        bool rollbackRequired = false;
        try
        {
            bool isThirdParty = CoreUtils.IsThirdParty(registerRequestPayload.BrandName);

            // Do CRM Search Profile
            JObject crmSearchProfileResponse = await crmMethods.SearchUserInCRM(registerRequestPayload, CrmApiToken);


            // If Search Profile Fails - Return
            if (crmSearchProfileResponse["status"]?.ToString() != SUCCESS_STATUS)
            {
                Logger.LogInfo($"Registration Failed at CRM Search Profile");
                return CoreHelpers.CustomErrorHelper.AuthenticationError(INTERNAL_SERVER_ERROR_MSG);
            }


            // Search Profile is success 
            JObject data = (JObject)(crmSearchProfileResponse["data"] ?? new JObject());

            string? userType = data.SelectToken("records[0].AccountRegistrationStatus__c")?.ToString()??"";
            string lastname="";
            string title="";
            if (userType == "Manual")
            {
                lastname = data.SelectToken("records[0].LastName")?.ToString() ?? "";
                title = data.SelectToken("records[0].Salutation")?.ToString() ?? "";
            }
           
            // If user is alredy registered - return
            if (userType == "Registered")
            {
                Logger.LogInfo($"Registration Failure, Account Already Exists ");
                if (!TargetStatus.IsWmRequired)
                {
                    CookieResponse cookieResponse = new CookieResponse();
                    cookieResponse.SingleProfileLatCookie = "-";
                    cookieResponse.SingleProfileUserToken = "-";
                    cookieResponse.SingleProfileLtCookie = "-";

                    JObject responsePayload = new()
                    {
                        ["message"] = ACCOUNT_ALREADY_EXISTS,
                        ["accountStatus"] = SUCCESS_STATUS,
                        ["oldCustomerID"] = "-",
                        ["newCustomerID"] = crmSearchProfileResponse?["data"]?["records"]?[0]?["PersonContactId"]!.ToString(),
                        ["cookies"] = JObject.FromObject(cookieResponse)
                    };

                    Logger.LogInfo($"Registration Failure (Account Already Exists), Response Payload: {responsePayload}");

                    return new()
                    {
                        ApiStatusCode = 200,
                        CustomResponsePayload = responsePayload
                    };
                }
                return CoreHelpers.CustomErrorHelper.AuthenticationError(ACCOUNT_ALREADY_EXISTS);

            }

            // User is Guest, Manual or New

            string oldCustomerID = "";
            string newCustomerID = "";
            CookieResponse cookies;
            JObject userDetails;

            SingleProfileSegmentResponse responseFromSingleProfile = await SingleProfileSegment(registerRequestPayload, isThirdParty, userType,(lastname,title));
            oldCustomerID = responseFromSingleProfile.OldCustomerID;
            cookies = responseFromSingleProfile.Cookies;
            userDetails = responseFromSingleProfile.GetUserResponse;
            createUserSingleProfileResponse = responseFromSingleProfile.CreateUserResponse;
            singleProfileToken = responseFromSingleProfile.SingleProfileToken;
            rollbackRequired = responseFromSingleProfile.RollbackRequired;

            // Single Profile Api Calls - Create and Get - Failure
            if (!responseFromSingleProfile.IsSuccess)
            {
                return CoreHelpers.CustomErrorHelper.AuthenticationError(responseFromSingleProfile.ResponseMessage);
            }


            // Check if user needs to be Created in CRM or Updated
            if (crmSearchProfileResponse.SelectToken("data.totalSize")?.ToString() == "0")
            {
                JObject model = new()
                {
                    ["content"] = new JObject()
                    {
                        ["crm_search_profile"] = crmSearchProfileResponse["data"],
                        ["currentTime"] = registerRequestPayload.Timestamp,
                        ["req_payload"] = JObject.FromObject(registerRequestPayload),
                        ["sp_get_profile"] = (JObject)(userDetails["data"] ?? new JObject()),
                        ["version"] = $"{CoreUtils.GetAppSettings("CRM_API_VERSION")}",
                        ["isTargetState"] = !TargetStatus.IsWmRequired
                    }
                };
                // Transform Paylod to CRM Request
                JObject transformationResponse = transformationHelper.Transformer("crmCreateNewUser.liquid", model, functionRootPath);


                // if Transformation Failure - Rollback and Return
                if (transformationResponse["status"]?.ToString() != SUCCESS_STATUS)
                {
                    Logger.LogInfo("Registration Failed at CRM Create Profile Transformation, Rolling back");
                    _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName); //target state
                    return CoreHelpers.CustomErrorHelper.AuthenticationError(INTERNAL_SERVER_ERROR_MSG);
                }

                // Transformation Success
                JObject transformedResponse = JObject.Parse(transformationResponse["transformedContent"]!.ToString());


                // Create user in CRM
                JObject crmResponse = await crmMethods.CreateUserInCRM(transformedResponse, CrmApiToken);

                // Create user in CRM Failed - Rollback and Return
                if (crmResponse["status"]?.ToString() == FAILED_STATUS)
                {

                    Logger.LogInfo("Registration Failed at CRM Create Profile, Rolling back");
                    _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName);
                    return CoreHelpers.CustomErrorHelper.AuthenticationError(INTERNAL_SERVER_ERROR_MSG);

                }

                // Create user in CRM success - Response and Return
                Logger.LogInfo("Registration Success !");
                newCustomerID = crmResponse["newCustomerID"]!.ToString();
                string? location = createUserSingleProfileResponse["location"]?.ToString();

                _ = singleProfileMethods.CreateSingleProfileMembership(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName, registerRequestPayload.RewardsOptIn);
                return PrepareResponsePayload(SUCCESS_MSG, SUCCESS_STATUS, newCustomerID, oldCustomerID, JObject.FromObject(cookies), 200, location);

            }

            Logger.LogInfo("Registration Update Profile Flow");
            JObject transformerInput = new()
            {
                ["content"] = new JObject()
                {
                    ["crm_search_profile"] = crmSearchProfileResponse["data"] ?? new JObject(),
                    ["currentTime"] = registerRequestPayload.Timestamp,
                    ["req_payload"] = JObject.FromObject(registerRequestPayload),
                    ["sp_get_profile"] = (JObject)(userDetails["data"] ?? new JObject()),
                    ["version"] = $"{CoreUtils.GetAppSettings("CRM_API_VERSION")}"
                }
            };

            // Transform payload to CRM Update user
            JObject transformationResponseUpdateUser = transformationHelper.Transformer("updateUserCRM.liquid", transformerInput, functionRootPath);

            // Transformation failed - Rollback and return
            if (transformationResponseUpdateUser["status"]?.ToString() != SUCCESS_STATUS)
            {
                Logger.LogInfo("Registration Failed at CRM Update Profile Transformation, Rolling back");
                _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName);
                return CoreHelpers.CustomErrorHelper.AuthenticationError(INTERNAL_SERVER_ERROR_MSG);
            }

            // Transformation Success
            JObject transformedResponseUpdateUser = JObject.Parse(transformationResponseUpdateUser["transformedContent"]!.ToString());

            // Call Update user in CRM 
            JObject crmResponseUpdateUser = await crmMethods.UpdateUserInCRM(transformedResponseUpdateUser, CrmApiToken);

            // Update user in CRM failed - Rollback and Return
            if (crmResponseUpdateUser["status"]?.ToString() == FAILED_STATUS)
            {

                Logger.LogInfo("Registration Failed at CRM Update Profile, Rolling back");
                _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName);
                return CoreHelpers.CustomErrorHelper.AuthenticationError(INTERNAL_SERVER_ERROR_MSG);

            }
            // Update user success - extract new Customer id and return response
            newCustomerID = crmResponseUpdateUser["newCustomerID"]!.ToString();
            string? locationHeaderValue = createUserSingleProfileResponse["location"]?.ToString();

            _ = singleProfileMethods.CreateSingleProfileMembership(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName, registerRequestPayload.RewardsOptIn);

            return PrepareResponsePayload(SUCCESS_MSG, SUCCESS_STATUS, newCustomerID, oldCustomerID, JObject.FromObject(cookies), 200, locationHeaderValue);





        }
        catch (Exception ex)
        {
            // if single profile has completed the create user flow, then rollback is required
            if (rollbackRequired)
            {

                string oldCustomerID = createUserSingleProfileResponse["data"]?.ToString() ?? "";
                _ = singleProfileMethods.DeleteUserInSingleProfile(oldCustomerID, singleProfileToken, registerRequestPayload.BrandName);
            }
            LogHandler("AZFU", ex, new LogTemplate
            {
                message = $"Registration Exception Failure {ex.Message} - {ex.StackTrace}",
            });

            return CoreHelpers.CustomErrorHelper.AuthenticationError(INTERNAL_SERVER_ERROR_MSG);

        }

    }
}
